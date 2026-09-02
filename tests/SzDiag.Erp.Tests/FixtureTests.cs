using System.Text.Json;
using System.Text.RegularExpressions;

namespace SzDiag.Erp.Tests;

/// <summary>
/// Фикстура — реальный ответ API, снятый с живой заявки и обезличенный. Строить DTO по
/// описанию, не видя настоящего ответа, нельзя: описание уже разошлось с реальностью
/// (комплектация и переписка пришли пустыми, збірка — null при готовой сборке).
/// </summary>
public class FixtureTests
{
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "sz-fetch.json");

    [Fact]
    public void Фикстура_на_месте_и_разбирается()
    {
        Assert.True(File.Exists(Path), $"Нет фикстуры: {Path}");

        using var document = JsonDocument.Parse(File.ReadAllText(Path));
        var request = document.RootElement.GetProperty("request");

        Assert.False(string.IsNullOrWhiteSpace(request.GetProperty("number").GetString()));
        Assert.True(request.GetProperty("fields").EnumerateObject().Any());
    }

    // Репозиторий публичный: персональные данные клиентов в фикстуре недопустимы.
    // Проверяем не «нет собаки в тексте» (заглушка её содержит), а что каждое
    // найденное вхождение — именно заглушка.

    [Fact]
    public void В_фикстуре_только_синтетическая_почта()
    {
        var found = Regex.Matches(File.ReadAllText(Path), @"[\w.+-]+@[\w-]+\.[\w.]+")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        Assert.All(found, value => Assert.Equal("someone@example.com", value));
    }

    [Fact]
    public void В_фикстуре_только_синтетические_телефоны()
    {
        var found = Regex.Matches(File.ReadAllText(Path), @"\+?380[\d\s\-()]{7,}\d")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        Assert.All(found, value => Assert.Equal("+380000000000", value));
    }

    [Fact]
    public void В_фикстуре_нет_живых_номеров_заявки_и_заказа()
    {
        var raw = File.ReadAllText(Path);

        Assert.DoesNotContain("161538", raw);
        Assert.DoesNotContain("1979212", raw);
    }
}
