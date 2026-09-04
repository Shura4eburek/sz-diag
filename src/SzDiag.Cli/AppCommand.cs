using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli app run|restart` — запуск GUI-приложения клиента elevated в его
/// интерактивной сессии без похода к машине.
///
/// Ритуал руками собирался трижды по разным заявкам (OCCT, TM5, SignalRGB) одной и той же
/// транзиентной scheduled-задачей: агент под SYSTEM живёт в session 0, `Start-Process` там
/// не создаёт окна, а без `/rl highest` приложениям, которым нужен доступ к SCM (SignalRGB —
/// свой драйвер), прилетает `OpenSCManager Error 5` (бэклог, пункт без номера).</summary>
public static class AppCommand
{
    /// <summary>Известные приложения для `app restart <СЗ> <имя>`: маска процессов для
    /// остановки, служба для подъёма, лаунчер. Список специально маленький — расширяется по
    /// мере заявок, как и вся остальная библиотека рецептов.</summary>
    public sealed record KnownApp(string ProcessMask, string ServiceName, string LauncherPath, bool Elevated);

    public static readonly IReadOnlyDictionary<string, KnownApp> KnownApps =
        new Dictionary<string, KnownApp>(StringComparer.OrdinalIgnoreCase)
        {
            // SignalRGB (первый живой кейс — п.164): без /rl highest приложение не видит свой
            // драйвер (OpenSCManager Error 5). Путь лаунчера и имя службы — типовая установка;
            // на конкретной машине могут отличаться, тогда используй --launcher.
            ["signalrgb"] = new(
                ProcessMask: "SignalRgb*",
                ServiceName: "SignalRgb.Service",
                LauncherPath: @"C:\Program Files\WhirlwindFX\SignalRgb\SignalRgb.exe",
                Elevated: true),
        };

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        return sub switch
        {
            "run" when args.Length >= 3 => await RunAppAsync(client, args),
            "restart" when args.Length >= 3 => await RestartAppAsync(client, args),
            _ => Usage(),
        };
    }

    private static async Task<int> RunAppAsync(IHubApiClient client, string[] args)
    {
        var sz = args[1];
        var exe = args[2];
        var wait = ArgValue(args, "--wait") is { } w && int.TryParse(w, out var ww) ? ww : 10;
        var elevated = args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));
        var extraArgs = ArgValue(args, "--args");

        var script = InteractiveSessionRun.BuildScript(sz, exe, extraArgs, elevated, wait);
        var res = await client.ExecAsync(sz, script, wait + 30);
        return PrintExecResult(res, sz);
    }

    private static async Task<int> RestartAppAsync(IHubApiClient client, string[] args)
    {
        var sz = args[1];
        var name = args[2];
        if (!KnownApps.TryGetValue(name, out var app))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неизвестное приложение:[/] {name}. Известны: {string.Join(", ", KnownApps.Keys)}");
            AnsiConsole.MarkupLine("[grey]Либо гони напрямую:[/] szcli app run <СЗ> <exe> [[--elevated]] [[--args \"...\"]]");
            return 2;
        }
        var launcher = ArgValue(args, "--launcher") ?? app.LauncherPath;

        // Гасим процессы → поднимаем службу → запускаем лаунчер в интерактивной сессии.
        // Три шага одной командой — раньше это была ad-hoc последовательность SSH-команд,
        // переписываемая заново на каждой заявке (бэклог, пункт без номера).
        var stopScript = $"Get-Process -Name '{app.ProcessMask.TrimEnd('*')}*' -ErrorAction SilentlyContinue | " +
                          "Stop-Process -Force -ErrorAction SilentlyContinue; " +
                          $"try {{ Start-Service -Name '{app.ServiceName}' -ErrorAction Stop; 'служба {app.ServiceName}: запущена' }} " +
                          $"catch {{ 'служба {app.ServiceName}: ' + $_.Exception.Message }}";
        var stopRes = await client.ExecAsync(sz, stopScript, 60);
        if (stopRes is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (!string.IsNullOrEmpty(stopRes.StdOut)) Console.WriteLine(CliXml.Decode(stopRes.StdOut).TrimEnd());

        var launchScript = InteractiveSessionRun.BuildScript(sz, launcher, null, app.Elevated, waitSeconds: 15);
        var launchRes = await client.ExecAsync(sz, launchScript, 60);
        return PrintExecResult(launchRes, sz);
    }

    private static int PrintExecResult(ExecResult? res, string sz)
    {
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (!string.IsNullOrEmpty(res.StdOut)) Console.WriteLine(CliXml.Decode(res.StdOut).TrimEnd());
        if (!string.IsNullOrEmpty(res.StdErr))
            AnsiConsole.MarkupLineInterpolated($"[yellow]stderr:[/] {CliXml.Decode(res.StdErr).TrimEnd()}");
        return res.ExitCode == 0 ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && args.Length > idx + 1 ? args[idx + 1] : null;
    }

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli app run <СЗ> <exe> [[--args "..."]] [[--elevated]] [[--wait N]]   запустить в интерактивной сессии
              szcli app restart <СЗ> <имя>   [[--launcher <exe>]]                    погасить/поднять службу/перезапустить лаунчер
            """);
        return 2;
    }
}
