using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SzDiag.Claude;

namespace SzDiag.Desk.Services;

public interface ITerminalLauncher
{
    bool Open(string key, string sessionId);
}

/// <summary>«Открыть в терминале»: `claude --resume` в Windows Terminal, без него — в обычной
/// консоли. Через .cmd-файл, а не аргументами: у уже запущенного Windows Terminal новая вкладка
/// получает окружение его процесса, и CLAUDE_CONFIG_DIR из Desk туда не доехал бы.</summary>
/// <param name="profileFor">Профиль сессии по ключу — тот, в чьём каталоге лежит разговор.</param>
/// <param name="workDirFor">Рабочий каталог разговора по ключу: --resume ищет разговор по нему.</param>
public sealed class TerminalLauncher(string? claudeExe, Func<string, string> workDirFor, Func<string, ClaudeProfile?> profileFor,
    string scriptDir) : ITerminalLauncher
{
    public static string Script(string claudeExe, string workDir, string? configDir, string sessionId)
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 >nul\r\n");
        // Маркеры сессии, из которой запущен Desk, — прочь (см. ClaudeLaunch.InheritedSessionMarkers).
        foreach (var name in ClaudeLaunch.InheritedSessionMarkers) sb.Append($"set \"{name}=\"\r\n");
        // Профиль по умолчанию — переменная снимается: иначе терминал унаследовал бы профиль того,
        // кто запустил Desk, и --resume искал бы разговор не в том каталоге.
        sb.Append($"set \"CLAUDE_CONFIG_DIR={configDir}\"\r\n");
        sb.Append($"cd /d \"{workDir}\"\r\n");
        sb.Append($"\"{claudeExe}\" --resume {sessionId}\r\n");
        return sb.ToString();
    }

    /// <summary>То же для macOS: `.command` открывается в Terminal через `open`.</summary>
    public static string MacScript(string claudeExe, string workDir, string? configDir, string sessionId)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder("#!/bin/bash\n");
        foreach (var name in ClaudeLaunch.InheritedSessionMarkers) sb.Append($"unset {name}\n");
        sb.Append(configDir is null ? "unset CLAUDE_CONFIG_DIR\n" : $"export CLAUDE_CONFIG_DIR={Q(configDir)}\n");
        sb.Append($"cd {Q(workDir)}\n");
        sb.Append($"{Q(claudeExe)} --resume {Q(sessionId)}\n");
        return sb.ToString();
    }

    public bool Open(string key, string sessionId)
    {
        if (claudeExe is null || profileFor(key) is not { } profile) return false;
        var configDir = profile.ConfigDir;
        var workDir = workDirFor(key);
        var mac = OperatingSystem.IsMacOS();
        string path;
        try
        {
            Directory.CreateDirectory(scriptDir);
            path = Path.Combine(scriptDir, $"resume-{key}.{(mac ? "command" : "cmd")}");
            File.WriteAllText(path, mac
                ? MacScript(claudeExe, workDir, configDir, sessionId)
                : Script(claudeExe, workDir, configDir, sessionId), new UTF8Encoding(false));
            if (mac) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (mac) return TryStart("/usr/bin/open", $"\"{path}\"");
        return TryStart("wt.exe", $"-d \"{workDir}\" cmd.exe /k \"{path}\"")
               || TryStart("cmd.exe", $"/c start \"claude {key}\" cmd.exe /k \"{path}\"");
    }

    private static bool TryStart(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
            return p is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
