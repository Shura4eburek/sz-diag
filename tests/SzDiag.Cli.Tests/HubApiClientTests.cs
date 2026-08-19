using System.Net;
using System.Net.Http.Json;
using System.Text;
using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class HubApiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _json;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode code, string json = "")
        {
            _code = code;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static HubApiClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://hub") };
        return new HubApiClient(http, "mgmt-token");
    }

    [Fact]
    public async Task GetSessions_ParsesJsonAndSendsToken()
    {
        var json = """
        [{"sz":"156864","ip":"10.0.0.42","hostname":"PC-1","status":0,
          "connectedAt":"2026-07-01T12:00:00+00:00","lastHeartbeat":"2026-07-01T12:00:00+00:00"}]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var client = NewClient(handler);

        var sessions = await client.GetSessionsAsync();

        Assert.Equal("156864", sessions.Single().Sz);
        Assert.Equal("mgmt-token", handler.LastRequest!.Headers.GetValues("X-SzDiag-Mgmt-Token").Single());
    }

    [Fact]
    public async Task AddNoteAsync_PostsTextToJournalEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        var ok = await client.AddNoteAsync("160697", "поставив тестовий Corsair RM850x");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/sessions/160697/journal", handler.LastRequest.RequestUri!.AbsolutePath);
        // Тело разбираем обратно, а не ищем подстроку: System.Text.Json экранирует кириллицу
        // в escape-последовательности — это валидный JSON, hub его читает, но в сыром теле
        // текста глазами не видно.
        var sent = await handler.LastRequest.Content!.ReadFromJsonAsync<JournalNoteRequest>();
        Assert.Equal("поставив тестовий Corsair RM850x", sent!.Text);
    }

    [Fact]
    public async Task AddNoteAsync_WhenHubRejects_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.BadRequest));

        Assert.False(await client.AddNoteAsync("160697", "текст"));
    }

    [Fact]
    public async Task Close_Ok_ReturnsTrue()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK));
        Assert.True(await client.CloseAsync("156864"));
    }

    [Fact]
    public async Task Close_NotFound_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.False(await client.CloseAsync("000000"));
    }

    [Fact]
    public async Task GetTarget_NotFound_ReturnsNull()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.Null(await client.GetTargetAsync("000000"));
    }

    [Fact]
    public async Task TriggerTest_Ok_ReturnsSuccess()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK));

        var result = await client.TriggerTestAsync("156864", null, "сток JEDEC 4800", false);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TriggerTest_NotFound_ReturnsFailure()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));

        Assert.False((await client.TriggerTestAsync("000000", null, "сток", false)).Ok);
    }

    [Fact]
    public async Task TriggerTest_SendsFilterAndConfigInBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        await client.TriggerTestAsync("156864", "occt", "EXPO 6000, штатний БЖ", false);

        Assert.Equal("/api/sessions/156864/test", handler.LastRequest!.RequestUri!.AbsolutePath);
        var sent = await handler.LastRequest.Content!.ReadFromJsonAsync<TestRunRequest>();
        Assert.Equal("occt", sent!.Filter);
        Assert.Equal("EXPO 6000, штатний БЖ", sent.Config);
        Assert.False(sent.SameConfig);
    }

    [Fact]
    public async Task TriggerTest_SameConfig_SendsFlagAndReturnsHubErrorText()
    {
        // Подсказка hub про --same-config обязана доехать до пользователя целиком.
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            "прогон без метки конфигурации не запускается; повторить ту же: --same-config");
        var client = NewClient(handler);

        var result = await client.TriggerTestAsync("156864", "occt", null, true);

        Assert.False(result.Ok);
        Assert.Contains("--same-config", result.Error);
        var sent = await handler.LastRequest!.Content!.ReadFromJsonAsync<TestRunRequest>();
        Assert.True(sent!.SameConfig);
    }

    [Fact]
    public async Task TriggerDiag_Ok_ReturnsTrue()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK));
        Assert.True(await client.TriggerDiagAsync("156864"));
    }

    [Fact]
    public async Task TriggerDiag_WithSections_AppendsQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        await client.TriggerDiagAsync("156864", "storage,events");

        Assert.Contains("/api/sessions/156864/diag", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("sections=storage", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public void Ctor_SnimaetDefoltniyTaymautHttpClient()
    {
        // Регрессия (СЗ 160450): дефолтные 100 секунд HttpClient.Timeout обрывали exec,
        // которому через --timeout отвели 3000 — 8-гигабайтная закачка на агенте падала
        // с TaskCanceledException на 100-й секунде, хотя скрипт продолжал работать.
        // Срок теперь отмеряется на каждом вызове, а не общим потолком клиента.
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK)) { BaseAddress = new Uri("http://hub") };

        _ = new HubApiClient(http, "mgmt-token");

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }
}
