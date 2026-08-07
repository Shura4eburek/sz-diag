using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using SzDiag.Agent;
using SzDiag.ConsoleUi;
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

// Единый лок на запись в консоль: липкая панель перерисовывается из таймер-потока, логи и
// вывод Spectre — из своих. Оборачиваем именно rawOut, чтобы под локом оказались оба пути
// вывода (Tee для логов и Spectre для цветного).
var consoleGate = new object();
var rawOut = new SyncedConsoleWriter(Console.Out, consoleGate);
Console.SetOut(new TeeTextWriter(rawOut, logFile));

// Цветной вывод (Spectre.Console) идёт напрямую в реальную консоль, минуя Tee — иначе в
// лог-файл попадали бы сырые ANSI-коды. Announce() дублирует туда же чистый текст без разметки.
var term = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(rawOut) });
void Announce(string plain, string? markup = null)
{
    term.MarkupLine(markup ?? Markup.Escape(plain));
    logFile.WriteLine(plain);
}

// UTF-8 для дочернего PowerShell включается сам по среде: на обычной винде — да (иначе
// кириллица в diag.md/exec превращается в кракозябры), в WinPE — нет (там присвоение
// [Console]::OutputEncoding вешает powershell.exe, см. WinPeEnvironment).
// Агент — служебный процесс и обязан отвечать именно тогда, когда машине плохо. Под OCCT
// Extreme на всех ядрах он конкурирует с нагрузкой на равных и не получает квант: на 160636
// три exec подряд ушли в таймаут при живом heartbeat (бэклог п.33/п.43).
try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
catch { /* нет прав на смену приоритета — не критично */ }

var ps = new PowerShellRunner();

// Режим watchdog / автозакрытие: sz-agent --revert <statePath>.
// Ничему тут падать нельзя: на 160705 этот режим умер на необработанном System.IO —
// доступ остался на машине навсегда, а watchdog-задача отстрелялась и второй попытки не
// было (бэклог п.59). Поэтому: всё в try/catch, полный текст ошибки в файл рядом с
// state.json, при неудаче — перевзвод watchdog и ненулевой код возврата.
if (args.Length >= 2 && args[0] == "--revert")
{
    var revertLog = RevertLog.Open(args[1]);
    try
    {
        var st = RevertStateStore.Load(args[1]);
        if (st is null)
        {
            revertLog.Write("state.json отсутствует — откатывать нечего.");
            return 0;
        }

        var revertOpts = new AgentOptions();
        config.Bind(revertOpts);

        // Watchdog не должен резать доступ под работающей сессией: на 160306 он снёс sshd,
        // учётку и state.json ровно через час, пока шли exec/pull и heartbeat, и никто этого
        // не заметил — CLI продолжал показывать «online · готов» (бэклог п.85/п.81).
        // Ручной откат от hub идёт с --force: там метка живости роли не играет, доступ
        // снимают намеренно.
        var forced = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        var lastSeen = AccessLiveness.LastSeen(args[1]);
        var now = DateTimeOffset.Now;
        revertLog.Write($"СЗ {st.Sz}: {AccessLiveness.Explain(lastSeen, st.OpenedAt, now)}");
        if (!forced && !AccessLiveness.ShouldRevert(lastSeen, st.OpenedAt, now))
        {
            // Агент на связи — переносим срок и уходим, ничего не трогая. Если перевзвести
            // задачу не удалось, откатываем сейчас: доступ без сторожа опаснее прерванной работы.
            var rescheduled = false;
            try
            {
                ps.Run(WindowsSystemAccessManager.BuildWatchdogTaskCommand(
                    st.WatchdogTaskName, Environment.ProcessPath!, args[1],
                    DateTime.Now.AddHours(revertOpts.WatchdogHours)), throwOnError: false);
                rescheduled = true;
            }
            catch (Exception ex)
            {
                revertLog.Write($"не удалось перевзвести watchdog ({ex.Message}) — откатываю сейчас.");
            }

            if (rescheduled)
            {
                logFile.WriteLine($"[watchdog] СЗ {st.Sz}: агент жив — откат отложен, watchdog перевзведён " +
                                  $"на +{revertOpts.WatchdogHours} ч.");
                logFile.Flush();
                return 0;
            }
        }

        var revertSshd = new PortableSshServer(
            Path.IsPathRooted(revertOpts.SshBinDir)
                ? revertOpts.SshBinDir
                : Path.Combine(AppContext.BaseDirectory, revertOpts.SshBinDir),
            revertOpts.SshWorkDir, ps);

        var outcome = new WindowsSystemAccessManager(ps, revertSshd, args[1]).Revert(st);
        AccessLiveness.Delete(args[1]);
        revertLog.Write($"СЗ {st.Sz}: {outcome.Summary()}");
        logFile.WriteLine($"[revert] СЗ {st.Sz}: {outcome.Summary()}");
        logFile.Flush();
        if (outcome.AllClean) return 0;

        // Не всё откатилось — даём себе вторую попытку через 10 минут вместо «N/A» в
        // расписании задачи. Шаги идемпотентны, повтор безопасен.
        try
        {
            ps.Run(WindowsSystemAccessManager.BuildWatchdogTaskCommand(
                st.WatchdogTaskName, Environment.ProcessPath!, args[1],
                DateTime.Now.AddMinutes(10)), throwOnError: false);
            revertLog.Write("watchdog перевзведён на +10 минут для повторной попытки.");
        }
        catch (Exception ex)
        {
            revertLog.Write($"не удалось перевзвести watchdog: {ex}");
        }
        return 1;
    }
    catch (Exception ex)
    {
        // Сюда попадать не должно (шаги изолированы), но если попали — след обязан остаться.
        revertLog.Write($"ОТКАТ УПАЛ ЦЕЛИКОМ: {ex}");
        try { logFile.WriteLine($"[revert] упал целиком: {ex}"); logFile.Flush(); } catch { }
        return 1;
    }
    finally { revertLog.Dispose(); }
}

// Второй агент на машине — всегда авария: дерётся за лог и state.json, а первый при этом
// перестаёт слать heartbeat (СЗ 160306 час висела offline при живой машине). Режим --revert
// сюда не попадает намеренно: watchdog откатывает доступ как раз при живом агенте.
using var instanceGuard = SingleInstanceGuard.Acquire();
if (!instanceGuard.IsPrimary)
{
    logFile.WriteLine("Агент уже запущен на этой машине — второй экземпляр не нужен.");
    logFile.WriteLine("Закрой окно первого агента или сними задачу szdiag-autostart-<СЗ>.");
    logFile.Flush();
    term.MarkupLine("[red]Агент уже запущен на этой машине.[/] Второй экземпляр не запускается.");
    return 3;
}

// Режим возобновления после ребута: agent.exe --resume <statePath>. Поднимается
// автостарт-задачей под SYSTEM (headless, без консоли). Переподнимает sshd и
// реконнектится под тем же СЗ из persisted state; живёт до отката от hub/watchdog.
if (args.Length >= 2 && args[0] == "--resume")
{
    var state = RevertStateStore.Load(args[1]);
    if (state is null)
    {
        logFile.WriteLine("[resume] state.json отсутствует — возобновлять нечего.");
        logFile.Flush();
        return 0;
    }

    var rOpts = new AgentOptions();
    config.Bind(rOpts);
    string R(string p) => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);

    var rPubKey = File.ReadAllText(R(rOpts.ServicePublicKeyPath));
    var rSpec = new AccessSpec(state.Sz, rOpts.ServiceAccount, rPubKey, rOpts.SshPort,
        TimeSpan.FromHours(rOpts.WatchdogHours));
    var rSshd = new PortableSshServer(R(rOpts.SshBinDir), rOpts.SshWorkDir, ps);
    var rManager = new WindowsSystemAccessManager(ps, rSshd, args[1]);

    var rHubUrl = rOpts.HubUrl;
    if (string.IsNullOrWhiteSpace(rHubUrl))
    {
        try { rHubUrl = await HubDiscovery.FindHubAsync(rOpts.AgentToken); }
        catch (HubNotFoundException ex)
        {
            logFile.WriteLine($"[resume] hub не найден: {ex.Message}");
            logFile.Flush();
            return 1; // автостарт повторит при следующем ребуте
        }
    }

    var rLink = new SignalRHubLink(rHubUrl, rOpts.AgentToken);
    // Разбор прошлого выключения уезжает на hub вместе с boot-time: без него нажатие
    // кнопки питания попадало в счётчик вырубонов наравне с обрывом (бэклог п.93).
    var rBoot = BootTimeReader.Read(ps);
    var rSession = new AgentSession(rManager, rLink, rSpec, Environment.MachineName,
        rBoot, ShutdownClassifier.Read(ps, rBoot));

    // Ребут мог случиться быстрее, чем поднялась сеть — bounded-ретрай подъёма.
    const int maxAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            logFile.WriteLine($"[resume] СЗ {state.Sz}: переподнимаю доступ (попытка {attempt})…");
            await rSession.ResumeAsync(state);
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logFile.WriteLine($"[resume] попытка {attempt} не удалась: {ex.Message}; retry через 30с");
            logFile.Flush();
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    logFile.WriteLine($"[resume] СЗ {state.Sz}: online (после ребута).");
    logFile.Flush();
    try { await rLink.ReportActivityAsync(state.Sz, "— готов (после ребута)", null); } catch { }

    AgentCommandWiring.RegisterHandlers(rLink, Environment.MachineName, ps,
        R(rOpts.TestSuitePath), (plain, _) => { logFile.WriteLine(plain); logFile.Flush(); },
        rHubUrl, rOpts.AgentToken);

    using var rCts = new CancellationTokenSource();
    var rHeartbeat = AgentCommandWiring.StartHeartbeatLoop(rSession, (int)rOpts.HeartbeatSeconds,
        rCts.Token, null, args[1],
        () => logFile.WriteLine("[resume] доступ снят снаружи — агент завершается."));

    await rSession.Completion; // ждём отката от hub (close) или watchdog
    rCts.Cancel();
    try { await rHeartbeat; } catch { }
    logFile.WriteLine($"[resume] СЗ {state.Sz}: сессия закрыта, откат выполнен.");
    logFile.Flush();
    return 0;
}

// Режим WinPE: agent.exe --pe [СЗ]. Загрузились с флешки в чистый PE — доступ не
// открываем (в PE нет SAM/Task Scheduler/фаервола, см. WinPeAccessManager), sshd не
// поднимаем. Работаем поверх исходящего SignalR: exec + diag. Ключ сервиса и
// testsuite.json здесь не обязательны — их может не быть на флешке.
if (args.Length >= 1 && args[0] == "--pe")
{
    term.Write(new Rule("[bold]sz-diag agent (WinPE)[/]").LeftJustified());

    var peSz = args.Length >= 2 ? args[1].Trim() : "";
    if (string.IsNullOrWhiteSpace(peSz))
    {
        term.Markup("Введите номер [yellow]СЗ[/]: ");
        peSz = (Console.ReadLine() ?? "").Trim();
    }
    logFile.WriteLine($"[pe] СЗ: {peSz}");
    if (!SzNumber.IsValid(peSz))
    {
        var peWhy = SzNumber.Explain(peSz);
        Announce($"Некорректный номер СЗ: {peWhy}.", $"[red]Некорректный номер СЗ:[/] {Markup.Escape(peWhy)}.");
        return 1;
    }

    var peOpts = new AgentOptions();
    config.Bind(peOpts);
    string PeResolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);

    var peKeyPath = PeResolve(peOpts.ServicePublicKeyPath);
    var peKey = File.Exists(peKeyPath) ? File.ReadAllText(peKeyPath) : "";
    var peSpec = new AccessSpec(peSz, peOpts.ServiceAccount, peKey, peOpts.SshPort,
        TimeSpan.FromHours(peOpts.WatchdogHours));

    var peHubUrl = peOpts.HubUrl;
    if (string.IsNullOrWhiteSpace(peHubUrl))
    {
        Announce("Ищу hub в сети…", "[grey]Ищу hub в сети…[/]");
        try
        {
            peHubUrl = await HubDiscovery.FindHubAsync(peOpts.AgentToken);
            Announce($"Hub найден: {peHubUrl}", $"Hub найден: [green]{peHubUrl}[/]");
        }
        catch (HubNotFoundException ex)
        {
            Announce($"{ex.Message} Сеть в PE поднимается через wpeinit — если её нет, " +
                     "в образ не инжектнуты драйверы сетевой карты.",
                $"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    var peLink = new SignalRHubLink(peHubUrl, peOpts.AgentToken);
    var peBoot = BootTimeReader.Read(ps);
    var peSession = new AgentSession(new WinPeAccessManager(), peLink, peSpec,
        Environment.MachineName, peBoot, ShutdownClassifier.Read(ps, peBoot));

    // В PE сеть — самое хрупкое место: драйвер NIC мог подняться позже (net-up
    // доставляет его с флешки) или DHCP ещё не отдал адрес. Без ретрая агент
    // просто падал стектрейсом «network unreachable» (живая СЗ 159948).
    const int peMaxAttempts = 5;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await peSession.StartAsync();
            break;
        }
        catch (Exception ex) when (attempt < peMaxAttempts)
        {
            Announce($"Hub {peHubUrl} не отвечает ({ex.Message}). Попытка {attempt}/{peMaxAttempts}, повтор через 10с…",
                $"[yellow]Hub[/] {Markup.Escape(peHubUrl)} [yellow]не отвечает[/] " +
                $"({Markup.Escape(ex.Message)}). Попытка {attempt}/{peMaxAttempts}, повтор через 10с…");
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Запасной вариант: адрес из конфига мог протухнуть (хост переехал,
            // другая подсеть) — после пары промахов ищем hub броадкастом.
            if (attempt == 2 && !string.IsNullOrWhiteSpace(peOpts.HubUrl))
            {
                Announce("Ищу hub броадкастом…", "[grey]Ищу hub броадкастом…[/]");
                try
                {
                    var found = await HubDiscovery.FindHubAsync(peOpts.AgentToken);
                    if (!string.Equals(found, peHubUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        Announce($"Hub найден: {found} (вместо {peHubUrl})",
                            $"Hub найден: [green]{Markup.Escape(found)}[/] (вместо {Markup.Escape(peHubUrl)})");
                        peHubUrl = found;
                        peLink = new SignalRHubLink(peHubUrl, peOpts.AgentToken);
                        peSession = new AgentSession(new WinPeAccessManager(), peLink, peSpec,
                            Environment.MachineName, peBoot, ShutdownClassifier.Read(ps, peBoot));
                    }
                }
                catch (HubNotFoundException dex)
                {
                    Announce($"Броадкаст hub не нашёл: {dex.Message}",
                        $"[yellow]Броадкаст hub не нашёл:[/] {Markup.Escape(dex.Message)}");
                }
            }
        }
        catch (Exception ex)
        {
            // Стектрейс .NET на экране PE ничего не даёт — говорим по делу.
            Announce($"Не достучались до hub {peHubUrl}: {ex.Message}\n" +
                     "Проверь сеть в PE: команда net-up (покажет адаптер, IP и пинг hub). " +
                     "Если адаптера нет — драйвер сетевой карты не поднялся.",
                $"[red]Не достучались до hub[/] {Markup.Escape(peHubUrl)}: {Markup.Escape(ex.Message)}\n" +
                "Проверь сеть в PE: [yellow]net-up[/] (адаптер, IP, пинг hub). " +
                "Если адаптера нет — драйвер сетевой карты не поднялся.");
            logFile.Flush();
            return 1;
        }
    }

    Announce($"СЗ {peSz}: WinPE ● online. Хост {Environment.MachineName}.",
        $"СЗ {peSz}: WinPE [green]● online[/]. Хост {Environment.MachineName}.");
    try { await peLink.ReportActivityAsync(peSz, "— готов (WinPE)", null); } catch { }

    AgentCommandWiring.RegisterHandlers(peLink, Environment.MachineName, ps,
        PeResolve(peOpts.TestSuitePath), (plain, markup) => Announce(plain, markup),
        peHubUrl, peOpts.AgentToken);

    // Панель в PE: watchdog не ставится (в PE нет Task Scheduler — см. WinPeAccessManager),
    // поэтому WatchdogAt = null и в панели будет прочерк.
    DateTimeOffset? peLastHeartbeatOk = null;
    using var peSticky = StickyHeader.TryStart(
        width => AgentStatusLine.Render(new AgentStatusContext(
            Sz: peSz,
            HubUrl: peHubUrl,
            SshPort: peOpts.SshPort,
            WatchdogAt: null,
            BootTime: BootTimeReader.Read(ps),
            LastHeartbeatOk: peLastHeartbeatOk,
            HeartbeatTimeout: TimeSpan.FromSeconds(peOpts.HeartbeatSeconds * 3),
            Mode: "WinPE",
            Now: DateTimeOffset.Now), width),
        new StickyOptions(Lines: 2, ConfigEnabled: peOpts.StickyHeader),
        new SystemTerminalSurface(rawOut),
        SystemTerminalSurface.TryEnableVirtualTerminal(),
        consoleGate);

    using var peCts = new CancellationTokenSource();
    var peHeartbeat = AgentCommandWiring.StartHeartbeatLoop(peSession, (int)peOpts.HeartbeatSeconds,
        peCts.Token, ok => { if (ok) peLastHeartbeatOk = DateTimeOffset.Now; });

    // При липкой панели хоткеи живут в ней — в поток их не печатаем, чтобы не дублировать.
    if (peSticky is null) term.MarkupLine("\n[green][[C]][/] Закрыть СЗ    [grey][[Q]][/] Выход");
    logFile.WriteLine("\n[C] Закрыть СЗ    [Q] Выход");
    while (true)
    {
        var peKeyPressed = Console.ReadKey(intercept: true).Key;
        if (peKeyPressed == ConsoleKey.C)
        {
            Announce("\nЗакрываю СЗ…", "\n[yellow]Закрываю СЗ…[/]");
            await peSession.RevertAsync();
            break;
        }
        if (peKeyPressed == ConsoleKey.Q) break;
    }

    peCts.Cancel();
    try { await peHeartbeat; } catch (OperationCanceledException) { }
    logFile.Flush();
    return 0;
}

// Объявлена до try, чтобы finally мог сбросить область прокрутки консоли.
StickyHeader? sticky = null;
try
{

term.Write(new Rule("[bold]sz-diag agent[/]").LeftJustified());
term.Markup("Введите номер [yellow]СЗ[/]: ");
var sz = (Console.ReadLine() ?? "").Trim();
logFile.WriteLine($"Введите номер СЗ: {sz}");
// Формат номера — ровно 6 цифр: hub по нему заводит скелет заметок, и опечатка оседает
// в базе знаний отдельной папкой-призраком (бэклог п.57).
if (!SzNumber.IsValid(sz))
{
    var why = SzNumber.Explain(sz);
    Announce($"Некорректный номер СЗ: {why}.", $"[red]Некорректный номер СЗ:[/] {Markup.Escape(why)}.");
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
var sessionBoot = BootTimeReader.Read(ps);
var session = new AgentSession(manager, link, spec, Environment.MachineName,
    sessionBoot, ShutdownClassifier.Read(ps, sessionBoot));

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
    ResolvePath(opts.TestSuitePath), (plain, markup) => Announce(plain, markup),
    hubUrl, opts.AgentToken);

// Липкая панель статуса. Момент открытия доступа — точка отсчёта watchdog.
var openedAt = DateTimeOffset.Now;
DateTimeOffset? lastHeartbeatOk = null;
var bootTime = BootTimeReader.Read(ps);
sticky = StickyHeader.TryStart(
    width => AgentStatusLine.Render(new AgentStatusContext(
        Sz: sz,
        HubUrl: hubUrl,
        SshPort: opts.SshPort,
        WatchdogAt: openedAt + TimeSpan.FromHours(opts.WatchdogHours),
        BootTime: bootTime,
        LastHeartbeatOk: lastHeartbeatOk,
        HeartbeatTimeout: TimeSpan.FromSeconds(opts.HeartbeatSeconds * 3),
        Mode: "",
        Now: DateTimeOffset.Now), width),
    new StickyOptions(Lines: 2, ConfigEnabled: opts.StickyHeader),
    new SystemTerminalSurface(rawOut),
    SystemTerminalSurface.TryEnableVirtualTerminal(),
    consoleGate);

// Перехват закрытия окна консоли (крестик) → откат.
using var closeGuard = new ConsoleCloseGuard(() =>
{
    sticky?.Dispose();
    session.RevertAsync().GetAwaiter().GetResult();
});

// Пока агент жив, машина не уходит в простойный сон: на 160705 она уснула посреди
// диагностики, diag.md не появился вовсе, а агент после пробуждения залип (бэклог п.58).
// Правок схемы питания не делаем — удержание исчезает вместе с процессом, откатывать нечего.
if (SleepGuard.Prevent())
    logFile.WriteLine("Сон машины подавлен на время сессии (power request).");
else
    Announce("Не удалось подавить сон — машина может уснуть посреди прогона.", null);

// Heartbeat в фоне.
using var cts = new CancellationTokenSource();
// statePath отдаём циклу: он отмечает живость агента (watchdog по ней не режет доступ под
// работающей сессией) и замечает уже выполненный откат — тогда врать hub «online» нельзя.
var accessRevoked = false;
var heartbeat = AgentCommandWiring.StartHeartbeatLoop(session, (int)opts.HeartbeatSeconds,
    cts.Token, ok => { if (ok) lastHeartbeatOk = DateTimeOffset.Now; },
    opts.StatePath,
    () =>
    {
        accessRevoked = true;
        Announce("Доступ снят (watchdog или ручной откат) — агент завершается, сессия закрыта.",
            "[yellow]Доступ снят (watchdog или ручной откат)[/] — агент завершается.");
        cts.Cancel();
    });

// Сторож командного канала: heartbeat живёт отдельным лёгким путём и не замечает, что
// exec залип, — а снаружи это неотличимо от здорового агента.
var channelWatchdog = AgentCommandWiring.StartChannelWatchdog(ps, $"szdiag-autostart-{sz}",
    cts.Token, (plain, markup) => Announce(plain, markup));

// При липкой панели хоткеи живут в ней — в поток их не печатаем, чтобы не дублировать.
if (sticky is null)
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
try { await channelWatchdog; } catch (OperationCanceledException) { }
SleepGuard.Allow();
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
    sticky?.Dispose();   // сброс области прокрутки — иначе консоль остаётся усечённой
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
