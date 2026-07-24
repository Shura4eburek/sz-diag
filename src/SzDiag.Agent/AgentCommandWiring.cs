namespace SzDiag.Agent;

/// <summary>Общая для интерактивной и resume-веток проводка: обработчики RunTests/RunDiag
/// и фоновый heartbeat-цикл. announce(plain, markup) — вывод (markup=null → без разметки).</summary>
public static class AgentCommandWiring
{
    public static void RegisterHandlers(
        IHubLink link, string hostname, PowerShellRunner ps,
        string testSuitePath, Action<string, string?> announce)
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
    }

    /// <summary>Фоновый heartbeat-цикл до отмены. Ошибки глотаются (SignalR переподключается).</summary>
    public static Task StartHeartbeatLoop(AgentSession session, int heartbeatSeconds, CancellationToken ct) =>
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await session.HeartbeatOnceAsync(ct); } catch { /* переподключение SignalR */ }
                try { await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        });
}
