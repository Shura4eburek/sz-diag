using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Сырой stdout `claude` из спайка (Fixtures/*.jsonl) — парсер и сессия проверяются на
/// настоящих строках, а не на придуманных.</summary>
internal static class Fixture
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static IReadOnlyList<string> Lines(string name)
        => File.ReadAllLines(PathOf(name)).Where(l => l.Trim().Length > 0).ToList();

    public static IReadOnlyList<ClaudeEvent> Events(string name)
        => Lines(name).SelectMany(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch)).ToList();

    /// <summary>Первая строка фикстуры, из которой разобралось событие нужного вида.</summary>
    public static string Line(string name, Func<ClaudeEvent, bool> match)
        => Lines(name).First(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch).Any(match));
}
