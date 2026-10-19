namespace SzDiag.Claude;

public static class ClaudeLocator
{
    /// <summary>Явный путь из конфига — только он (молча подменить его найденным в PATH значило бы
    /// запустить не тот claude); пусто — ищем claude.exe в PATH. Нативный установщик кладёт именно
    /// exe, а .cmd-обёртки пришлось бы запускать через cmd /c.</summary>
    public static string? Resolve(string? configured, string? pathVariable)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
