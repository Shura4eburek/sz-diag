using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SzDiag.Desk.Services;

public interface ITerminalLauncher
{
    bool Open(string key, string sessionId);
}

/// <summary>«Открыть в терминале»: `claude --resume` в Windows Terminal, без него — в обычной
/// консоли. Через .cmd-файл, а не аргументами: у уже запущенного Windows Terminal новая вкладка
/// получает окружение его процесса, и CLAUDE_CONFIG_DIR из Desk туда не доехал бы.</summary>
public sealed class TerminalLauncher(string? claudeExe, string workDir, string? configDir, string scriptDir)
    : ITerminalLauncher
{
    public static string Script(string claudeExe, string workDir, string? configDir, string sessionId)
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 >nul\r\n");
        if (!string.IsNullOrEmpty(configDir)) sb.Append($"set \"CLAUDE_CONFIG_DIR={configDir}\"\r\n");
        sb.Append($"cd /d \"{workDir}\"\r\n");
        sb.Append($"\"{claudeExe}\" --resume {sessionId}\r\n");
        return sb.ToString();
    }

    public bool Open(string key, string sessionId)
    {
        if (claudeExe is null) return false;
        string path;
        try
        {
            Directory.CreateDirectory(scriptDir);
            path = Path.Combine(scriptDir, $"resume-{key}.cmd");
            File.WriteAllText(path, Script(claudeExe, workDir, configDir, sessionId), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
