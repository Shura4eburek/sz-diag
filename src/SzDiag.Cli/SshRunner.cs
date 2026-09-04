using System.Diagnostics;
using System.Text;

namespace SzDiag.Cli;

/// <summary>Путь в обход exec-канала: гоняет PowerShell-скрипт на клиенте через настоящий SSH,
/// а не через SignalR-команду агенту.
///
/// Боль (бэклог п.206, СЗ 162367): `Set-NetAdapterAdvancedProperty` ресетнул сетевой адаптер,
/// exec-канал агента не поднялся сам (heartbeat шёл, `szcli exec` — таймаут), а
/// `szcli agent restart` не помог **по построению**: он сам идёт через exec. Больше часа СЗ
/// была online и неуправляема. `szcli target` уже печатает рабочую SSH-строку (host-ключ,
/// `-o StrictHostKeyChecking=no`) — та же сборка команды годится и для того, чтобы гонять
/// команду напрямую, не подходя к машине.</summary>
public static class SshRunner
{
    /// <summary>PowerShell понимает `-EncodedCommand` как base64 от UTF-16LE текста —
    /// тот же формат, которым `ssh-run.sh` обходит проблемы с кавычками/кодировками на пути
    /// bash → ssh → cmd → powershell.</summary>
    public static string EncodeCommand(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script ?? ""));

    /// <summary>Аргументы для `ssh`: та же сборка, что и `szcli target` (host-ключ, отключённая
    /// проверка known_hosts — сессия каждой СЗ со своим одноразовым ключом), плюс сама команда.</summary>
    public static IReadOnlyList<string> BuildArgs(string user, string ip, string? keyPath, string script)
    {
        var args = new List<string>
        {
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=NUL",
            "-o", "ConnectTimeout=15",
        };
        if (keyPath is not null) { args.Add("-i"); args.Add(keyPath); }
        args.Add($"{user}@{ip}");
        args.Add($"powershell -NoProfile -EncodedCommand {EncodeCommand(script)}");
        return args;
    }

    /// <summary>Итог прогона: код возврата ssh (не PowerShell — тот сам не выставляет
    /// $LASTEXITCODE наружу через -EncodedCommand без явного `exit`), вывод, ошибка.</summary>
    public sealed record SshResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false);

    public static async Task<SshResult> RunAsync(IReadOnlyList<string> args, int timeoutSeconds = 30)
    {
        var psi = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* уже мёртв — не критично */ }
            return new SshResult(-1, "", "ssh не ответил за отведённое время", TimedOut: true);
        }

        return new SshResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
