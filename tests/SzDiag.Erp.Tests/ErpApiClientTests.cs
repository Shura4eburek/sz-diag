using System.Net;

namespace SzDiag.Erp.Tests;

public class ErpApiClientTests
{
    private static ErpApiClient ClientOver(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, "тест-токен");

    [Fact]
    public async Task Успешный_вызов_возвращает_поле_result()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.OK, """{"result":{"ok":true}}"""));
        using var client = ClientOver(handler);

        var result = await client.CallAsync("sz.fetch", new { number = "160800" });

        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Результат_переживает_освобождение_документа()
    {
        // JsonDocument освобождается сразу после разбора: без Clone() элемент протухает
        // и падает уже у вызывающего, вдалеке от причины.
        using var handler = new StubHandler((_, _) =>
            (HttpStatusCode.OK, """{"result":{"request":{"number":"160800"}}}"""));
        using var client = ClientOver(handler);

        var result = await client.CallAsync("sz.fetch");
        GC.Collect();

        Assert.Equal("160800", result.GetProperty("request").GetProperty("number").GetString());
    }

    [Fact]
    public async Task Имя_и_аргументы_уезжают_в_теле_запроса()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.OK, """{"result":{}}"""));
        using var client = ClientOver(handler);

        await client.CallAsync("sz.fetch", new { number = "160800" });

        Assert.Contains("sz.fetch", handler.Calls[0]);
        Assert.Contains("160800", handler.Calls[0]);
    }

    [Fact]
    public async Task Ошибка_превращается_в_исключение_с_кодом()
    {
        using var handler = new StubHandler((_, _) =>
            (HttpStatusCode.Conflict, """{"code":"client_not_logged_in","message":"видно вікно логіну"}"""));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("client_not_logged_in", error.Code);
        Assert.Contains("вікно логіну", error.Message);
    }

    [Fact]
    public async Task Нечитаемое_тело_ошибки_не_валит_клиент()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.InternalServerError, "<html>500</html>"));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("internal", error.Code);
    }

    [Fact]
    public async Task Ответ_без_поля_result_это_сбой_а_не_пустота()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.OK, """{"щось":"інше"}"""));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("internal", error.Code);
    }

    [Fact]
    public async Task Недоступный_сервис_это_не_живой_а_не_исключение()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("нет соединения"));
        using var client = ClientOver(handler);

        Assert.False(await client.IsAliveAsync());
    }

    [Fact]
    public async Task Недоступный_сервис_в_вызове_даёт_код_unavailable()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("нет соединения"));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("unavailable", error.Code);
    }

    [Fact]
    public void Без_адреса_в_конфиге_клиент_не_собирается()
    {
        var error = Assert.Throws<ErpApiException>(() => ErpApiClient.Create(new ErpOptions()));

        Assert.Equal("unavailable", error.Code);
    }

    [Fact]
    public void Без_файла_токена_клиент_не_собирается()
    {
        var options = new ErpOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            TokenFile = Path.Combine(Path.GetTempPath(), "нет-такого-файла-" + Guid.NewGuid().ToString("N")),
        };

        var error = Assert.Throws<ErpApiException>(() => ErpApiClient.Create(options));

        Assert.Equal("no_token", error.Code);
    }
}
