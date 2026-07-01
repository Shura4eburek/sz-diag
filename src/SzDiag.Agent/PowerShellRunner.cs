using System.Diagnostics;

namespace SzDiag.Agent;

public sealed record PsResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Запуск PowerShell-команд. Кидает при ненулевом коде, если throwOnError.</summary>
public sealed class PowerShellRunner
{
    public PsResult Run(string script, bool throwOnError = true)
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
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell завершился с кодом {p.ExitCode}: {stderr}");

        return new PsResult(p.ExitCode, stdout, stderr);
    }
}
