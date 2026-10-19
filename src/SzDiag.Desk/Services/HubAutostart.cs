using System.Diagnostics;
using System.Net;

namespace SzDiag.Desk.Services;

/// <summary>Hub стартует вместе с Desk, но не как его дочерний процесс: `start-hub.cmd` в своём
/// окне. Hub — канал к клиентским машинам и живёт сам по себе: закрыл Desk — агенты не теряют
/// связь, `szcli` и терминальный Claude работают дальше. Поднимаем только свой (localhost) и
/// только если он не отвечает.</summary>
public sealed class HubAutostart(Func<CancellationToken, Task<bool>> isUp, Func<string, bool> start)
{
    public static HubAutostart Default(Func<CancellationToken, Task<bool>> isUp) => new(isUp, StartDetached);

    /// <returns>Строка для лога Desk: что сделали и почему.</returns>
    public async Task<string> EnsureAsync(string hubUrl, string? script, CancellationToken ct)
    {
        if (!IsLocal(hubUrl)) return $"hub {hubUrl} не на этой машине — автозапуск не нужен";
        if (await isUp(ct).ConfigureAwait(false)) return "hub уже работает";
        if (script is null) return "hub не отвечает, а start-hub.cmd рядом нет — собери dist (tools\\build-dist.ps1)";
        return start(script)
            ? $"hub не отвечал — запущен {script} (отдельным окном, закрытие Desk его не остановит)"
            : $"hub не отвечал, а {script} не запустился";
    }

    public static bool IsLocal(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.IsLoopback || (IPAddress.TryParse(u.Host, out var ip) && IPAddress.IsLoopback(ip)));

    /// <summary>`dist\host\desk\SzDiag.Desk.exe` → `dist\host\start-hub.cmd`; иначе dist репозитория.</summary>
    public static string? FindScript(string baseDir, string workDir)
    {
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(baseDir))?.FullName;
        foreach (var candidate in new[]
                 {
                     parent is null ? null : Path.Combine(parent, "start-hub.cmd"),
                     Path.Combine(workDir, "dist", "host", "start-hub.cmd"),
                 })
            if (candidate is not null && File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>Через оболочку — своё консольное окно, без связи с Desk (ни перенаправления, ни job).</summary>
    private static bool StartDetached(string script)
    {
        try
        {
            Process.Start(new ProcessStartInfo(script)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
