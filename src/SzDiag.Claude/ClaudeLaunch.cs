using System.Diagnostics;
using System.Text;

namespace SzDiag.Claude;

/// <summary>Всё, с чем запускается один процесс `claude` сессии.</summary>
/// <param name="WorkDir">Корень репозитория sz-diag: там CLAUDE.md, .claude/settings.json, скиллы.</param>
/// <param name="ConfigDir">CLAUDE_CONFIG_DIR — профиль (логин, память); null — унаследовать.</param>
public sealed record ClaudeLaunch(string Executable, string WorkDir, string? ConfigDir, string? ResumeSessionId,
    string AppendSystemPrompt, string McpConfigPath)
{
    public const string PermissionTool = "mcp__desk__permission_prompt";

    /// <summary>Переменные, которыми Claude Code помечает свою сессию. Desk, запущенный из сессии
    /// Claude (dotnet run под Claude), иначе передал бы их детям: CHILD_SESSION выключает запись
    /// транскрипта (и --resume потом нечего продолжать), MESSAGING_SOCKET/TOKEN ведут в канал
    /// чужой сессии. Профиль (CLAUDE_CONFIG_DIR) маркером не считается.</summary>
    public static readonly IReadOnlyList<string> InheritedSessionMarkers = new[]
    {
        "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_EFFORT",
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<string> Arguments()
    {
        var a = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            // Без явного default у пользователя действует auto, и permission_prompt не зовётся вовсе (спайк).
            "--permission-mode", "default",
            "--permission-prompt-tool", PermissionTool,
            "--mcp-config", McpConfigPath,
            "--append-system-prompt", AppendSystemPrompt,
        };
        if (ResumeSessionId is { Length: > 0 } id)
        {
            a.Add("--resume");
            a.Add(id);
        }
        return a;
    }

    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var arg in Arguments()) psi.ArgumentList.Add(arg);
        foreach (var name in InheritedSessionMarkers) psi.Environment.Remove(name);
        if (ConfigDir is { Length: > 0 } dir) psi.Environment["CLAUDE_CONFIG_DIR"] = dir;
        return psi;
    }
}
