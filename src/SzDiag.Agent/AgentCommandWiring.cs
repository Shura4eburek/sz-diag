using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Общая для интерактивной и resume-веток проводка: обработчики RunTests/RunDiag
/// и фоновый heartbeat-цикл. announce(plain, markup) — вывод (markup=null → без разметки).</summary>
public static class AgentCommandWiring
{
    /// <param name="hubUrl">Адрес hub, с которого агент качает инструменты (`szcli push`).
    /// Пусто — доставка не регистрируется (нечем качать).</param>
    /// <param name="agentToken">Токен для HTTP-раздачи инструментов (тот же, что у SignalR).</param>
    public static void RegisterHandlers(
        IHubLink link, string hostname, PowerShellRunner ps,
        string testSuitePath, Action<string, string?> announce,
        string? hubUrl = null, string? agentToken = null)
    {
        // RunTests: по команде hub прогнать набор из testsuite.json и залить отчёт.
        if (File.Exists(testSuitePath))
        {
            var suite = TestSuite.Load(testSuitePath);
            var reportRunner = new TestReportRunner(
                new TestRunner(new PowerShellCommandExecutor(ps), new GdiScreenCapturer()),
                suite, link, hostname);
            link.OnRunTests(async (runSz, filter) =>
            {
                var scope = string.IsNullOrWhiteSpace(filter) ? "полный прогон" : $"фильтр {filter}";
                announce($"Прогон тестов для СЗ {runSz} ({scope})…", null);
                try
                {
                    var outcome = await reportRunner.RunAndUploadAsync(runSz, filter);
                    if (!outcome.Ran)
                    {
                        var ids = string.Join(", ", outcome.AvailableIds);
                        announce($"Не найдено шагов по фильтру '{filter}'. Доступные: {ids}", null);
                        await link.ReportActivityAsync(runSz, "— готов", null);
                    }
                    else
                    {
                        announce("Отчёт залит на hub.", null);
                        var mark = outcome.AllClean ? "✓" : "⚠";
                        await link.ReportActivityAsync(runSz, $"готов · последний: {outcome.RanLabel} {mark}", null);
                    }
                }
                catch (Exception ex)
                {
                    announce($"Ошибка прогона: {ex.Message}", null);
                    try { await link.ReportActivityAsync(runSz, "готов · последний: ошибка ⚠", null); } catch { }
                }
            });
        }

        // RunDiag: read-only снапшот (каталог проб встроен — работает всегда).
        var diagRunner = new DiagReportRunner(
            new TestRunner(new PowerShellCommandExecutor(ps), new GdiScreenCapturer()),
            DiagnosticProbes.Suite, link, hostname);
        link.OnRunDiag(async (runSz, sections) =>
        {
            var scope = string.IsNullOrWhiteSpace(sections) ? "все секции" : $"секции {sections}";
            announce($"Диагностика СЗ {runSz} ({scope})…", null);
            try
            {
                var outcome = await diagRunner.RunAndUploadAsync(runSz, sections);
                if (!outcome.Ran)
                {
                    var s = string.Join(", ", outcome.AvailableSections);
                    announce($"Не найдено секций по фильтру '{sections}'. Доступные: {s}", null);
                    await link.ReportActivityAsync(runSz, "— готов", null);
                }
                else
                {
                    announce("Диаг-отчёт залит на hub.", null);
                    await link.ReportActivityAsync(runSz, $"готов · диагностика: {outcome.RanLabel}", null);
                }
            }
            catch (Exception ex)
            {
                announce($"Ошибка диагностики: {ex.Message}", null);
                try { await link.ReportActivityAsync(runSz, "готов · диагностика: ошибка ⚠", null); } catch { }
            }
        });

        // Exec: ad-hoc PowerShell по запросу хоста — замена SSH для сбора данных.
        // Обработчик сам НЕ бросает: любая ошибка уходит ответом, иначе вызывающий на хосте
        // просто ждал бы таймаута, не понимая, что случилось.
        var execHandler = new ExecCommandHandler(ps);
        link.OnExec(async req =>
        {
            announce($"Exec на СЗ {req.Sz} ({req.Script.Length} символов, таймаут {req.TimeoutSeconds}с)…", null);
            var result = execHandler.Handle(req);
            try { await link.SendExecResultAsync(result); }
            catch (Exception ex) { announce($"Не смог вернуть результат exec: {ex.Message}", null); }
        });

        // Push: скачать инструмент с hub (клиент тянет с хоста — как в модели угроз).
        // Регистрируем только когда известен адрес hub: без него качать неоткуда.
        if (!string.IsNullOrWhiteSpace(hubUrl))
        {
            var (toolsDir, movedOutOfCloud) = ToolsDirectory.Resolve(AppContext.BaseDirectory);
            var http = new HttpClient { BaseAddress = new Uri(hubUrl), Timeout = Timeout.InfiniteTimeSpan };
            if (!string.IsNullOrEmpty(agentToken))
                http.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, agentToken);
            var pushHandler = new PushCommandHandler(http, toolsDir, movedOutOfCloud);

            if (movedOutOfCloud)
                announce($"Папка агента внутри облачного каталога — инструменты уйдут в {toolsDir}", null);

            link.OnPush(async req =>
            {
                announce($"Доставка '{req.Tool}' на СЗ {req.Sz} → {pushHandler.ToolsDir}…", null);
                PushResult result;
                try { result = await pushHandler.HandleAsync(req); }
                catch (Exception ex)
                {
                    result = new PushResult(req.RequestId, pushHandler.ToolsDir, 0, 0, 0, ex.Message);
                }

                announce(result.Error is null
                    ? $"Доставлено: {result.Downloaded} файлов ({result.Bytes / 1024 / 1024} МБ), пропущено {result.Skipped}."
                    : $"Доставка не удалась: {result.Error}", null);
                try { await link.SendPushResultAsync(result); }
                catch (Exception ex) { announce($"Не смог вернуть итог доставки: {ex.Message}", null); }
            });
        }

        // Pull: отдать хосту файлы с клиента (дампы, CSV, логи) — вместо ручного base64
        // через ssh. Как и exec, никогда не молчит: ошибка уходит в PullResult.
        var pullHandler = new PullCommandHandler(
            (chunk, ct) => link.SendPullChunkAsync(chunk, ct));
        link.OnPull(async req =>
        {
            announce($"Забор файлов для СЗ {req.Sz}: {req.Path}", null);
            PullResult result;
            try { result = await pullHandler.HandleAsync(req); }
            catch (Exception ex)
            {
                result = new PullResult(req.RequestId, Array.Empty<PullFileInfo>(), ex.Message);
            }

            var sent = result.Files.Count(f => !f.Skipped);
            announce($"Забор завершён: отдано файлов {sent} из {result.Files.Count}.", null);
            try { await link.SendPullResultAsync(result); }
            catch (Exception ex) { announce($"Не смог вернуть итог забора: {ex.Message}", null); }
        });
    }

    /// <summary>Фоновый сторож командного канала: раз в <paramref name="intervalSeconds"/>
    /// гоняет тривиальный PowerShell тем же путём, что и exec. Несколько неудач подряд —
    /// канал завис (машина проснулась из сна, воркер залип), и агент лечится перезапуском
    /// через автостарт-задачу: снаружи такой агент выглядит здоровым, heartbeat идёт
    /// (бэклог п.58), поэтому заметить это может только он сам.</summary>
    /// <param name="autostartTaskName">Задача, которой поднимаем себя заново; пусто —
    /// только сообщаем о зависании (в PE автостарт-задачи нет).</param>
    public static Task StartChannelWatchdog(IPowerShellRunner ps, string? autostartTaskName,
        CancellationToken ct, Action<string, string?> announce,
        int intervalSeconds = 120, int probeTimeoutSeconds = 90, int failuresBeforeHeal = 3) =>
        Task.Run(async () =>
        {
            var watchdog = new CommandChannelWatchdog(failuresBeforeHeal);
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct); }
                catch (OperationCanceledException) { break; }

                var alive = CommandChannelWatchdog.Probe(ps, TimeSpan.FromSeconds(probeTimeoutSeconds));
                if (!watchdog.Observe(alive))
                {
                    if (!alive)
                        announce($"Командный канал не ответил ({watchdog.ConsecutiveFailures} подряд) — слежу.", null);
                    continue;
                }

                announce($"Командный канал завис ({watchdog.ConsecutiveFailures} проб подряд).", null);
                if (string.IsNullOrWhiteSpace(autostartTaskName))
                {
                    watchdog.Reset();   // лечиться нечем — не спамим каждую итерацию
                    continue;
                }

                announce($"Перезапускаюсь через задачу {autostartTaskName}…", null);
                try
                {
                    ps.Run(CommandChannelWatchdog.BuildSelfHealCommand(autostartTaskName),
                        throwOnError: false, timeout: TimeSpan.FromSeconds(30));
                    Environment.Exit(2);   // старый экземпляр обязан уйти: мьютекс держит слот
                }
                catch (Exception ex)
                {
                    announce($"Самолечение не удалось: {ex.Message}", null);
                    watchdog.Reset();
                }
            }
        }, ct);

    /// <summary>Фоновый heartbeat-цикл до отмены. Ошибки глотаются (SignalR переподключается).</summary>
    /// <param name="onResult">Необязательный колбэк с исходом каждой попытки — панель
    /// статуса по нему отличает живой канал от переподключения.</param>
    public static Task StartHeartbeatLoop(AgentSession session, int heartbeatSeconds,
        CancellationToken ct, Action<bool>? onResult = null) =>
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var ok = false;
                try { await session.HeartbeatOnceAsync(ct); ok = true; }
                catch { /* переподключение SignalR */ }
                try { onResult?.Invoke(ok); } catch { /* панель не должна ронять цикл */ }
                try { await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        });
}
