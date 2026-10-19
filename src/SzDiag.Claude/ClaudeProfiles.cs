namespace SzDiag.Claude;

/// <param name="Name">Имя каталога без точки: `claude`, `claude2`.</param>
/// <param name="ConfigDir">CLAUDE_CONFIG_DIR; null — профиль по умолчанию (~/.claude).</param>
public sealed record ClaudeProfile(string Name, string? ConfigDir);

/// <summary>Профили Claude на машине — ищутся, а не хардкодятся: на боксе их два (~/.claude и
/// ~/.claude2, второй поднимает обёртка claude2.cmd), и у каждого свой логин и своя память.</summary>
public static class ClaudeProfiles
{
    /// <summary>Каталоги `.claude*` в домашней папке, в которых есть вход (`.credentials.json`).
    /// Так отсекаются соседи вроде `.claude-tg-bridge`, у которых только настройки.</summary>
    public static IReadOnlyList<ClaudeProfile> Discover(string home)
    {
        if (!Directory.Exists(home)) return Array.Empty<ClaudeProfile>();
        try
        {
            return new DirectoryInfo(home).EnumerateDirectories(".claude*")
                .Where(d => File.Exists(Path.Combine(d.FullName, ".credentials.json")))
                .Select(d => new ClaudeProfile(d.Name.TrimStart('.'),
                    d.Name.Equals(".claude", StringComparison.OrdinalIgnoreCase) ? null : d.FullName))
                // Профиль по умолчанию — первым: его берёт «Начать сессию», когда выбирать не из чего.
                .OrderBy(p => p.ConfigDir is null ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ClaudeProfile>();
        }
    }
}
