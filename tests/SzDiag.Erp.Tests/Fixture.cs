using System.Text.Json;

namespace SzDiag.Erp.Tests;

internal static class Fixture
{
    private static readonly JsonDocument Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sz-fetch.json")));

    /// <summary>Реальный ответ по живой заявке, обезличенный.</summary>
    public static JsonElement SzFetch() => Doc.RootElement;

    /// <summary>Ответ, собранный на лету: для случаев, которых в фикстуре нет.</summary>
    public static JsonElement Of(string json) => JsonDocument.Parse(json).RootElement;
}
