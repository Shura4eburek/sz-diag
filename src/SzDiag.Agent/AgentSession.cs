using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Оркестрация сессии агента: открыть доступ, подключиться, регистрировать,
/// слать heartbeat, идемпотентно откатывать по любому триггеру.</summary>
public sealed class AgentSession
{
    private readonly ISystemAccessManager _manager;
    private readonly IHubLink _link;
    private readonly AccessSpec _spec;
    private readonly string _hostname;
    private readonly DateTimeOffset? _bootTime;
    private readonly string? _lastShutdown;
    private readonly bool _foundHubByBroadcast;
    private readonly RevertCoordinator _coordinator;
    private RevertState? _state;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Завершается после отката (hub/watchdog/локально) — сигнал headless-режиму выйти.</summary>
    public Task Completion => _completed.Task;

    /// <param name="bootTime">Время загрузки ОС (см. <see cref="BootTimeReader"/>): уезжает на
    /// hub при регистрации, чтобы тот отличал реальный ребут от лага heartbeat.</param>
    /// <param name="lastShutdown">Чем закончилась прошлая сессия ОС (см.
    /// <see cref="ShutdownClassifier"/>): hub по нему отличает обрыв питания от выключения
    /// кнопкой и не считает второе вырубоном (бэклог п.93).</param>
    /// <param name="foundHubByBroadcast">Hub найден UDP-broadcast'ом — машина в одной сети с
    /// боксом, и подключаться к ней надо напрямую, а не через туннель.</param>
    public AgentSession(ISystemAccessManager manager, IHubLink link, AccessSpec spec, string hostname,
        DateTimeOffset? bootTime = null, string? lastShutdown = null,
        bool foundHubByBroadcast = false)
    {
        _manager = manager;
        _link = link;
        _spec = spec;
        _hostname = hostname;
        _bootTime = bootTime;
        _lastShutdown = lastShutdown;
        _foundHubByBroadcast = foundHubByBroadcast;
        _coordinator = new RevertCoordinator(DoRevertAsync);
    }

    /// <summary>Сообщить hub, чем машина доступна. Зовётся после каждой регистрации: и при
    /// открытии доступа, и после ребута, где имя туннеля уже другое.</summary>
    private Task ReportAccessAsync(CancellationToken ct)
        => _state is null
            ? Task.CompletedTask
            : _link.ReportAccessAsync(
                AccessReporter.BuildReport(_state, _spec.Sz, _foundHubByBroadcast), ct);

    /// <summary>Регистрация + отчёт о доступе. Зовётся при старте, при возобновлении и
    /// ЗАНОВО после каждого реконнекта: hub адресует команды по ConnectionId последней
    /// регистрации, а `WithAutomaticReconnect()` после обрыва даёт новый (СЗ 162003, п.273).
    /// Доступ при этом не переоткрывается — меняется только адрес.</summary>
    private async Task RegisterAndReportAsync(CancellationToken ct)
    {
        var secret = await _link.RegisterAsync(_spec.Sz, _hostname, _bootTime, _lastShutdown,
            AgentIdentity.CurrentUser(), AgentIdentity.CurrentSessionId(), ct);
        if (_state is not null) _manager.PersistSessionSecret(_state, secret);
        await ReportAccessAsync(ct);
    }

    /// <summary>Реконнект: перерегистрироваться, иначе hub продолжит звать закрытое
    /// соединение, а снаружи это выглядит как «heartbeat свежий, агент не отвечает».
    /// Падение здесь гасим: следующий реконнект (или heartbeat на hub, который тоже
    /// переставляет адрес) повторит попытку, а исключение из обработчика SignalR
    /// уронило бы реконнект целиком.</summary>
    private void WireReconnect()
        => _link.OnReconnected(async () =>
        {
            try
            {
                await RegisterAndReportAsync(CancellationToken.None);
                Console.WriteLine("соединение с hub восстановлено — агент перерегистрирован");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"реконнект: перерегистрация не удалась — {ex.Message}");
            }
        });

    public async Task StartAsync(CancellationToken ct = default)
    {
        _state = _manager.Open(_spec);
        _link.OnRevert(async _ => await _coordinator.TriggerAsync());
        WireReconnect();
        await _link.ConnectAsync(ct);
        await RegisterAndReportAsync(ct);
    }

    /// <summary>Возобновление после ребута: state загружен с диска, доступ переподнимается
    /// (Resume, не Open), дальше как обычная сессия — connect + register под тем же СЗ.</summary>
    public async Task ResumeAsync(RevertState loaded, CancellationToken ct = default)
    {
        _state = loaded;
        _manager.Resume(loaded, _spec);
        _link.OnRevert(async _ => await _coordinator.TriggerAsync());
        WireReconnect();
        await _link.ConnectAsync(ct);
        // После ребута имя туннеля новое — без ReportAccess hub остался бы с мёртвым адресом.
        await RegisterAndReportAsync(ct);
    }

    public Task HeartbeatOnceAsync(CancellationToken ct = default) => _link.HeartbeatAsync(_spec.Sz, ct);

    /// <summary>Локальный/watchdog/консоль-триггер отката.</summary>
    public Task RevertAsync() => _coordinator.TriggerAsync();

    private async Task DoRevertAsync()
    {
        if (_state is not null)
        {
            var outcome = _manager.Revert(_state);
            // Отправляем ДО DisposeAsync: закрыть канал раньше, чем сводка ушла, значит
            // потерять единственное подтверждение полноты отката, которое не требует похода
            // к машине (бэклог п.119). Отправка не должна ронять откат: канал мог уже быть
            // недоступен (сеть легла раньше отката), и тогда сводка просто теряется.
            try
            {
                var failed = outcome.Failed.Select(f => new RevertResultFailure(f.Step, f.Error)).ToList();
                // Таймаут короче дефолтного HubConnection.ServerTimeout (30 с, M-3, ревью
                // волны 1): без него зависший hub держал бы откат по клавише C полминуты без
                // объяснения — сводка и так best-effort, дольше нескольких секунд ждать смысла нет.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _link.SendRevertResultAsync(new RevertResult(_spec.Sz, outcome.Done.ToList(), failed), cts.Token);
            }
            catch { /* канал недоступен — откат всё равно выполнен */ }
        }
        await _link.DisposeAsync();
        _completed.TrySetResult();
    }
}
