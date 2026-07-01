using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using SzDiag.Agent;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZAGENT_")
    .Build();
var opts = new AgentOptions();
config.Bind(opts);

var ps = new PowerShellRunner();

// Режим watchdog / автозакрытие: sz-agent --revert <statePath>
if (args.Length >= 2 && args[0] == "--revert")
{
    var st = RevertStateStore.Load(args[1]);
    if (st is not null) new WindowsSystemAccessManager(ps, args[1]).Revert(st);
    return 0;
}

Console.Write("Введите номер СЗ: ");
var sz = (Console.ReadLine() ?? "").Trim();
if (string.IsNullOrWhiteSpace(sz) || !sz.All(char.IsDigit))
{
    Console.WriteLine("Некорректный номер СЗ.");
    return 1;
}

// Относительные пути (ключ, testsuite) — рядом с exe, независимо от рабочего каталога.
string ResolvePath(string p) => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);

var pubKey = File.ReadAllText(ResolvePath(opts.ServicePublicKeyPath));
var spec = new AccessSpec(sz, opts.ServiceAccount, pubKey, opts.SshPort,
    TimeSpan.FromHours(opts.WatchdogHours));

var manager = new WindowsSystemAccessManager(ps, opts.StatePath);
var link = new SignalRHubLink(opts.HubUrl, opts.AgentToken);
var session = new AgentSession(manager, link, spec, Environment.MachineName);

Console.WriteLine($"Открываю доступ для СЗ {sz}…");
await session.StartAsync();
Console.WriteLine($"СЗ {sz}: доступ открыт ● online. Хост {Environment.MachineName}.");

// Тест-раннер: по команде hub RunTests прогнать набор и залить отчёт.
var suitePath = ResolvePath(opts.TestSuitePath);
if (File.Exists(suitePath))
{
    var suite = TestSuite.Load(suitePath);
    var reportRunner = new TestReportRunner(
        new TestRunner(new PowerShellCommandExecutor(ps), new GdiScreenCapturer()),
        suite, link, Environment.MachineName);
    link.OnRunTests(async runSz =>
    {
        Console.WriteLine($"Прогон тестов для СЗ {runSz}…");
        try { await reportRunner.RunAndUploadAsync(runSz); Console.WriteLine("Отчёт залит на hub."); }
        catch (Exception ex) { Console.WriteLine($"Ошибка прогона: {ex.Message}"); }
    });
}

// Перехват закрытия окна консоли (крестик) → откат.
using var closeGuard = new ConsoleCloseGuard(() => session.RevertAsync().GetAwaiter().GetResult());

// Heartbeat в фоне.
using var cts = new CancellationTokenSource();
var heartbeat = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await session.HeartbeatOnceAsync(cts.Token); } catch { /* переподключение SignalR */ }
        try { await Task.Delay(TimeSpan.FromSeconds(opts.HeartbeatSeconds), cts.Token); }
        catch (OperationCanceledException) { break; }
    }
});

Console.WriteLine("\n[C] Закрыть СЗ и откатить    [Q] Выход без отката (не рекомендуется)");
while (true)
{
    var key = Console.ReadKey(intercept: true).Key;
    if (key == ConsoleKey.C)
    {
        Console.WriteLine("\nЗакрываю СЗ и откатываю…");
        await session.RevertAsync();
        break;
    }
    if (key == ConsoleKey.Q) break;
}

cts.Cancel();
try { await heartbeat; } catch (OperationCanceledException) { }
Console.WriteLine("Готово.");
return 0;

/// <summary>Ловит CTRL_CLOSE_EVENT (крестик окна) и запускает откат.</summary>
sealed class ConsoleCloseGuard : IDisposable
{
    private delegate bool HandlerRoutine(int ctrlType);
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    private readonly HandlerRoutine _handler;
    private readonly Action _onClose;

    public ConsoleCloseGuard(Action onClose)
    {
        _onClose = onClose;
        _handler = Handle;
        SetConsoleCtrlHandler(_handler, true);
    }

    private bool Handle(int ctrlType)
    {
        // 2 = CTRL_CLOSE_EVENT
        if (ctrlType == 2) _onClose();
        return true;
    }

    public void Dispose() => SetConsoleCtrlHandler(_handler, false);
}
