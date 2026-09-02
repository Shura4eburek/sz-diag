using System.Net;

namespace SzDiag.Erp.Tests;

public class ErpSessionTests
{
    private static StubHandler Ok() => new((_, _) => (HttpStatusCode.OK, """{"result":{}}"""));

    private static ErpApiClient ClientOver(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, "тест-токен");

    [Fact]
    public async Task Захват_берётся_и_отпускается()
    {
        using var handler = Ok();
        using var client = ClientOver(handler);

        await using (await ErpSession.BeginAsync(client)) { }

        Assert.Contains(ErpSession.BeginTool, handler.Calls[0]);
        Assert.Contains(ErpSession.EndTool, handler.Calls[^1]);
    }

    [Fact]
    public async Task Захват_отпускается_при_исключении_внутри()
    {
        using var handler = Ok();
        using var client = ClientOver(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = await ErpSession.BeginAsync(client);
            throw new InvalidOperationException("что-то пошло не так");
        });

        Assert.Contains(ErpSession.EndTool, handler.Calls[^1]);
    }

    [Fact]
    public async Task Повторный_Dispose_не_шлёт_второй_end()
    {
        using var handler = Ok();
        using var client = ClientOver(handler);

        var session = await ErpSession.BeginAsync(client);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, handler.Calls.Count(c => c.Contains(ErpSession.EndTool)));
    }

    [Fact]
    public async Task Занятый_захват_пробрасывается_наружу()
    {
        using var handler = new StubHandler((_, _) =>
            (HttpStatusCode.Conflict, """{"code":"busy","message":"вже йде сесія"}"""));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => ErpSession.BeginAsync(client));

        Assert.Equal("busy", error.Code);
    }

    [Fact]
    public async Task Неудачный_захват_не_шлёт_освобождение()
    {
        // Иначе отпустили бы чужую сессию, которая и заняла захват.
        using var handler = new StubHandler((_, _) =>
            (HttpStatusCode.Conflict, """{"code":"busy","message":"вже йде сесія"}"""));
        using var client = ClientOver(handler);

        await Assert.ThrowsAsync<ErpApiException>(() => ErpSession.BeginAsync(client));

        Assert.DoesNotContain(handler.Calls, c => c.Contains(ErpSession.EndTool));
    }

    [Fact]
    public async Task Сбой_освобождения_не_валит_вызывающий_код()
    {
        // Сервис умер посреди работы: end не пройдёт, но исключение из Dispose затмило бы
        // настоящую причину сбоя.
        using var handler = new StubHandler((_, body) => body.Contains(ErpSession.EndTool)
            ? (HttpStatusCode.InternalServerError, """{"code":"internal","message":"впав"}""")
            : (HttpStatusCode.OK, """{"result":{}}"""));
        using var client = ClientOver(handler);

        var session = await ErpSession.BeginAsync(client);
        await session.DisposeAsync();

        Assert.Equal(2, handler.Calls.Count);
    }
}
