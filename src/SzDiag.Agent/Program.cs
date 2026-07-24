using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using SzDiag.Agent;
using SzDiag.Contracts;

// UTF-8 в консоли — иначе кириллица превращается в «?» на Windows.
try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch { /* вывод может быть перенаправлен — не критично */ }

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZAGENT_")
    .Build();
var opts = new AgentOptions();
config.Bind(opts);

// Лог-файл рядом с exe: переживает падение и закрытие окна консоли.
var logPath = Path.IsPathRooted(opts.LogPath)
    ? opts.LogPath
    : Path.Combine(AppContext.BaseDirectory, opts.LogPath);
var logFile = AgentLog.Init(logPath);
var rawOut = Console.Out;
Console.SetOut(new TeeTextWriter(rawOut, logFile));

// Цветной вывод (Spectre.Console) идёт напрямую в реальную консоль, минуя Tee — иначе в
// лог-файл попадали бы сырые ANSI-коды. Announce() дублирует туда же чистый текст без разметки.
var term = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(rawOut) });
void Announce(string plain, string? markup = null)
{
    term.MarkupLine(markup ?? Markup.Escape(plain));
    logFile.WriteLine(plain);
}

var ps = new PowerShellRunner();

// Режим watchdog / автозакрытие: sz-agent --revert <statePath>
if (args.Length >= 2 && args[0] == "--revert")
{
    var st = RevertStateStore.Load(args[1]);
    if (st is not null)
    {
        var revertOpts = new AgentOptions();
        config.Bind(revertOpts);
        var revertSshd = new PortableSshServer(
            Path.IsPathRooted(revertOpts.SshBinDir)
                ? revertOpts.SshBinDir
                : Path.Combine(AppContext.BaseDirectory, revertOpts.SshBinDir),
            revertOpts.SshWorkDir, ps);
        new WindowsSystemAccessManager(ps, revertSshd, args[1]).Revert(st);
    }
    return 0;
}

try
{

term.Write(new Rule("[bold]sz-diag agent[/]").LeftJustified());
term.Markup("Введите номер [yellow]СЗ[/]: ");
var sz = (Console.ReadLine() ?? "").Trim();
logFile.WriteLine($"Введите номер СЗ: {sz}");
if (string.IsNullOrWhiteSpace(sz) || !sz.All(char.IsDigit))
{
    Announce("Некорректный номер СЗ.", "[red]Некорректный номер СЗ.[/]");
    return 1;
}

// Относительные пути (ключ, testsuite) — рядом с exe, независимо от рабочего каталога.
string ResolvePath(string p) => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);

var pubKey = File.ReadAllText(ResolvePath(opts.ServicePublicKeyPath));
var spec = new AccessSpec(sz, opts.ServiceAccount, pubKey, opts.SshPort,
    TimeSpan.FromHours(opts.WatchdogHours));

var sshBinDir = ResolvePath(opts.SshBinDir);
var sshd = new PortableSshServer(sshBinDir, opts.SshWorkDir, ps);
var manager = new WindowsSystemAccessManager(ps, sshd, opts.StatePath);

var hubUrl = opts.HubUrl;
if (string.IsNullOrWhiteSpace(hubUrl))
{
    Announce("Ищу hub в сети…", "[grey]Ищу hub в сети…[/]");
    try
    {
        hubUrl = await HubDiscovery.FindHubAsync(opts.AgentToken);
        Announce($"Hub найден: {hubUrl}", $"Hub найден: [green]{hubUrl}[/]");
    }
    catch (HubNotFoundException ex)
    {
        Announce(ex.Message, $"[red]{Markup.Escape(ex.Message)}[/]");
        return 1;
    }
}

var link = new SignalRHubLink(hubUrl, opts.AgentToken);
var session = new AgentSession(manager, link, spec, Environment.MachineName);

Announce($"Открываю доступ для СЗ {sz}…", $"[grey]Открываю доступ для СЗ {sz}…[/]");
try
{
    await session.StartAsync();
}
catch (SshdStartException ex)
{
    Announce($"Не удалось поднять SSH: {ex.Message}",
        $"[red]Не удалось поднять SSH:[/] {Markup.Escape(ex.Message)}");
    return 1;
}
Announce($"СЗ {sz}: доступ открыт ● online. Хост {Environment.MachineName}.",
    $"СЗ {sz}: доступ открыт [green]● online[/]. Хост {Environment.MachineName}.");

// Стартовая активность в таблице CLI: простаиваем, готовы к прогону.
try { await link.ReportActivityAsync(sz, "— готов", null); } catch { /* статус не критичен */ }

// Обработчики RunTests/RunDiag от hub (общие с resume-веткой — см. AgentCommandWiring).
AgentCommandWiring.RegisterHandlers(link, Environment.MachineName, ps,
    ResolvePath(opts.TestSuitePath), (plain, markup) => Announce(plain, markup));

// Перехват закрытия окна консоли (крестик) → откат.
using var closeGuard = new ConsoleCloseGuard(() => session.RevertAsync().GetAwaiter().GetResult());

// Heartbeat в фоне.
using var cts = new CancellationTokenSource();
var heartbeat = AgentCommandWiring.StartHeartbeatLoop(session, (int)opts.HeartbeatSeconds, cts.Token);

term.MarkupLine("\n[green][[C]][/] Закрыть СЗ и откатить    [grey][[Q]][/] Выход без отката (не рекомендуется)");
logFile.WriteLine("\n[C] Закрыть СЗ и откатить    [Q] Выход без отката (не рекомендуется)");
while (true)
{
    var key = Console.ReadKey(intercept: true).Key;
    if (key == ConsoleKey.C)
    {
        Announce("\nЗакрываю СЗ и откатываю…", "\n[yellow]Закрываю СЗ и откатываю…[/]");
        await session.RevertAsync();
        break;
    }
    if (key == ConsoleKey.Q) break;
}

cts.Cancel();
try { await heartbeat; } catch (OperationCanceledException) { }
Announce("Готово.", "[green]Готово.[/]");
return 0;

}
catch (Exception ex)
{
    term.WriteLine();
    logFile.WriteLine();
    term.Write(new Panel(Markup.Escape(ex.ToString()))
        .Header("[red bold]ФАТАЛ: агент упал[/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Red));
    logFile.WriteLine($"[ФАТАЛ] Агент упал: {ex}");
    Announce($"Лог сохранён: {logPath}", $"[grey]Лог сохранён:[/] {logPath}");
    term.MarkupLine("[grey]Нажмите любую клавишу для выхода…[/]");
    logFile.WriteLine("Нажмите любую клавишу для выхода…");
    try { Console.ReadKey(intercept: true); } catch { /* нет консоли — просто выходим */ }
    return 1;
}
finally
{
    logFile.Flush();
}

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
