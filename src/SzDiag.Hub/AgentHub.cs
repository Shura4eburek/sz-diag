using System.Text;
using Microsoft.AspNetCore.SignalR;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>SignalR-хаб для агентов. Тонкий слой над сервисами.</summary>
public sealed class AgentHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IKnowledgeBaseScaffolder _kb;
    private readonly IReportStore _reports;
    private readonly ExecCoordinator _exec;
    private readonly PullCoordinator _pull;
    private readonly PushCoordinator _push;
    private readonly JournalWriter _journal;

    public AgentHub(SessionRegistry registry, ISessionStore store,
        IKnowledgeBaseScaffolder kb, IReportStore reports, ExecCoordinator exec,
        PullCoordinator pull, PushCoordinator push, JournalWriter journal)
    {
        _registry = registry;
        _store = store;
        _kb = kb;
        _reports = reports;
        _exec = exec;
        _pull = pull;
        _push = push;
        _journal = journal;
    }

    public async Task Register(RegisterRequest request)
    {
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var outcome = _registry.Register(request.Sz, ip, request.Hostname, Context.ConnectionId,
            request.BootTime, request.LastShutdown);
        if (outcome.Rebooted)
        {
            // Пишем в SQLite сразу: in-memory реестр не переживает рестарт hub, а вырубон,
            // о котором никто не узнал, оборачивается ложным «дефект не воспроизведён»
            // (так на 160306 вырубон на нашем же стенде нашли лишь через неделю — п.55).
            var held = outcome.UptimeBefore is { } u ? $", продержалась {u:d\\.hh\\:mm\\:ss}" : "";
            var busy = outcome.ActivityBefore is { } a ? $", активность: {a}" : "";
            // Смена boot-time сама по себе вырубоном не является: агент присылает разбор
            // Kernel-Power 41, и выключение кнопкой в счётчик отказов не идёт (бэклог п.93).
            var failure = ShutdownKind.CountsAsFailure(request.LastShutdown);
            var label = failure ? "ВЫРУБОН" : "перезагрузка";
            Console.WriteLine($"[hub] СЗ {request.Sz}: {label} — клиент перезагрузился " +
                              $"({ShutdownKind.Describe(request.LastShutdown)}, " +
                              $"boot-time {request.BootTime:yyyy-MM-dd HH:mm:ss}){held}{busy}");
            // Журнал СЗ ведётся на украинском (как весь kb), поэтому слова свои, а не из
            // консольной строки hub. События машины обязаны попадать туда сами: команды с
            // хоста в этот момент нет, а без записи ход диагностики потом не восстановить.
            var heldUa = outcome.UptimeBefore is { } uUa
                ? $", протрималась {uUa:d\\.hh\\:mm\\:ss}" : "";
            var busyUa = outcome.ActivityBefore is { } aUa
                ? $", активність: {aUa}" : "";
            var labelUa = failure
                ? "вирубон"
                : "перезавантаження";
            _journal.Machine(request.Sz, $"**{labelUa}**{heldUa}{busyUa}");
            await _store.RecordRebootAsync(new RebootEvent(
                request.Sz, DateTimeOffset.UtcNow, outcome.PreviousBootTime, request.BootTime,
                (long?)outcome.UptimeBefore?.TotalSeconds, outcome.ActivityBefore,
                request.LastShutdown));
        }
        _kb.EnsureSkeleton(request.Sz);
        await _store.RecordOpenAsync(
            new SessionRecord(request.Sz, ip, request.Hostname, DateTimeOffset.UtcNow, null));
    }

    /// <summary>Агент принёс события питания из журнала клиента. Hub сливает их со своими:
    /// всё, что случилось до подключения агента, он увидеть не мог, и `reboots` печатал
    /// «вырубонов не зафиксировано» там, где они были (бэклог п.97).</summary>
    public async Task PowerEvents(PowerEventsReport report)
    {
        if (report.Events.Count == 0) return;
        var added = await _store.MergeJournalEventsAsync(report);
        if (added > 0)
            Console.WriteLine($"[hub] СЗ {report.Sz}: из журнала клиента добавлено событий питания: {added}");
    }

    public Task Heartbeat(string sz)
    {
        _registry.Heartbeat(sz);
        return Task.CompletedTask;
    }

    /// <summary>Агент вернул результат exec — отдаём его ожидающему HTTP-запросу CLI.</summary>
    public Task ExecResult(ExecResult result)
    {
        _exec.Complete(result);
        return Task.CompletedTask;
    }

    /// <summary>Агент подтвердил приём команды — до запуска скрипта.</summary>
    public Task ExecAck(ExecAck ack)
    {
        _exec.Acknowledge(ack);
        return Task.CompletedTask;
    }

    /// <summary>Агент прислал состояние фоновой задачи.</summary>
    public Task ExecJobStatus(ExecJobStatus status)
    {
        _exec.CompleteStatus(status);
        return Task.CompletedTask;
    }

    /// <summary>Агент отчитался о доставке инструмента.</summary>
    public Task PushResult(PushResult result)
    {
        _push.Complete(result);
        return Task.CompletedTask;
    }

    /// <summary>Агент прислал кусок забираемого файла.</summary>
    public Task PullChunk(PullChunk chunk)
    {
        _pull.AcceptChunk(chunk);
        return Task.CompletedTask;
    }

    /// <summary>Агент закончил забор — отдаём итог ожидающему запросу CLI.</summary>
    public Task PullResult(PullResult result)
    {
        _pull.Complete(result);
        return Task.CompletedTask;
    }

    public Task ReportActivity(string sz, string activity, DateTimeOffset? since)
    {
        _registry.SetActivity(sz, activity, since);
        return Task.CompletedTask;
    }

    public async Task UploadReportFile(UploadReportPart part)
    {
        var content = part.Content;

        // Отчёт собирает агент, а метку конфигурации знает только хост — дописываем здесь.
        // Без неё через неделю непонятно, на профиле гнали или на стоке (СЗ 160697).
        if (part.FileName.Equals("report.md", StringComparison.OrdinalIgnoreCase)
            && await _store.GetLastTestConfigAsync(part.Sz) is { } config)
        {
            var text = Encoding.UTF8.GetString(content);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var cut = text.IndexOf(nl, StringComparison.Ordinal);
            content = Encoding.UTF8.GetBytes(cut < 0
                ? $"{text}{nl}{nl}**Конфігурація прогону:** {config}{nl}"
                : $"{text[..cut]}{nl}{nl}**Конфігурація прогону:** {config}{text[cut..]}");
        }

        _reports.Save(part.Sz, part.Timestamp, part.FileName, content);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.MarkOfflineByConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
