using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SzDiag.Desk.Services;

public sealed record SzcliResult(int ExitCode, string Output);

public interface ISzcliRunner
{
    /// <summary>Путь к szcli.cmd; null — не найден.</summary>
    string? Location { get; }

    Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Действия инспектора и паспорт железа — тем же szcli, что в терминале: freeze хранит
/// прежние значения рядом с szcli, у close свои защиты, у sz fetch своя сессия захвата. Вторая
/// реализация тех же команд в Desk разошлась бы с первой при первой правке.</summary>
public sealed partial class SzcliRunner(Func<string?> szcliCmd) : ISzcliRunner
{
    public const int NotFound = -1;
    public const int Cancelled = -2;

    public string? Location => szcliCmd();

    /// <summary>szcli.cmd — обёртка над `cli\SzDiag.Cli.exe`; exe зовём напрямую, без cmd /c.</summary>
    public static string? ExeFor(string? szcliCmd)
    {
        if (szcliCmd is null) return null;
        var exe = Path.Combine(Path.GetDirectoryName(szcliCmd)!, "cli", "SzDiag.Cli.exe");
        return File.Exists(exe) ? exe : null;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    public static string StripAnsi(string s) => Ansi().Replace(s, "");

    public async Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = ExeFor(szcliCmd());
        if (exe is null)
            return new SzcliResult(NotFound,
                "szcli не найден: собери dist (tools\\build-dist.ps1) — Desk зовёт те же команды, что и терминал.");

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Spectre при NO_COLOR не пишет ESC-последовательности — в окне им не место.
        psi.Environment["NO_COLOR"] = "1";

        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Битый/заблокированный антивирусом exe — ответ в окне, а не падение Desk.
            return new SzcliResult(NotFound, $"szcli не запустился: {ex.Message}");
        }
        using var _ = p;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new SzcliResult(Cancelled, Combine(await stdout, await stderr) + "\n[прервано оператором]");
        }
        return new SzcliResult(p.ExitCode, Combine(await stdout, await stderr));
    }

    private static string Combine(string stdout, string stderr)
        => StripAnsi(stderr.Length == 0 ? stdout : $"{stdout}\n{stderr}").TrimEnd();
}
