using System.Text.Json;

namespace SzDiag.Agent;

/// <summary>Набор тестов из testsuite.json.</summary>
public sealed class TestSuite
{
    public IReadOnlyList<TestStep> Steps { get; init; } = Array.Empty<TestStep>();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TestSuite Load(string path)
        => JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(path), Opts) ?? new TestSuite();
}
