using SzDiag.Claude;

namespace SzDiag.Desk.Tests;

/// <summary>Фикстуры спайка (линк из SzDiag.Claude.Tests/Fixtures).</summary>
internal static class Fixture
{
    public static IReadOnlyList<string> Lines(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Where(l => l.Trim().Length > 0).ToList();

    public static IReadOnlyList<ClaudeEvent> Events(string name)
        => Lines(name).SelectMany(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch)).ToList();

    public static string Line(string name, Func<ClaudeEvent, bool> match)
        => Lines(name).First(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch).Any(match));
}
