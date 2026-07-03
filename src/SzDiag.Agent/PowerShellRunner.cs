using System.Diagnostics;

namespace SzDiag.Agent;

public sealed record PsResult(int ExitCode, string StdOut, string StdErr);

/// <summary>PowerShell-команда не уложилась в отведённый таймаут — процесс убит.</summary>
public sealed class PowerShellTimeoutException : Exception
{
    public PowerShellTimeoutException(string message) : base(message) { }
}

/// <summary>Запуск PowerShell-команд. Кидает при ненулевом коде, если throwOnError.</summary>
public sealed class PowerShellRunner
{
    public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine(script);
        p.StandardInput.Close();

        // Асинхронное чтение запущено ДО WaitForExit: синхронный ReadToEnd() блокируется
        // до EOF, которое наступает только при завершении процесса — с ним таймаут
        // никогда бы не сработал (мы бы зависли на самом чтении, а не дошли до ожидания).
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var exited = timeout is null
            ? p.WaitForExit(Timeout.Infinite)
            : p.WaitForExit((int)timeout.Value.TotalMilliseconds);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* уже мог сам завершиться — гонка */ }
            throw new PowerShellTimeoutException($"PowerShell не уложился в таймаут {timeout}: {script}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell завершился с кодом {p.ExitCode}: {stderr}");

        return new PsResult(p.ExitCode, stdout, stderr);
    }
}
