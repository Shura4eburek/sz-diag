using System.Net;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Headless-откат (watchdog, после ребута) уходит по HTTP без живого SignalR —
/// доказать владение СЗ там нечем, кроме секрета сессии: токен `/agent/*` общий на весь
/// флот, а за туннелем и IP у всех агентов одинаков.</summary>
public class RevertStatusSecretTests
{
    private sealed class ЛовушкаЗапроса : HttpMessageHandler
    {
        public HttpRequestMessage? Запрос { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Запрос = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (RevertStatusReporter Reporter, ЛовушкаЗапроса Ловушка) Собрать()
    {
        var ловушка = new ЛовушкаЗапроса();
        var http = new HttpClient(ловушка) { BaseAddress = new Uri("http://hub.example.com") };
        return (new RevertStatusReporter(http), ловушка);
    }

    [Fact]
    public async Task Секрет_уезжает_заголовком()
    {
        var (reporter, ловушка) = Собрать();

        await reporter.ReportAsync("162003", success: true, "всё снято", "секрет-сессии");

        Assert.True(ловушка.Запрос!.Headers.TryGetValues(HubRoutes.SessionSecretHeader, out var значения));
        Assert.Equal("секрет-сессии", Assert.Single(значения!));
    }

    [Fact]
    public async Task Без_секрета_заголовка_нет()
    {
        // Агент старой сборки секрета не получал — hub откатится на сверку по IP.
        var (reporter, ловушка) = Собрать();

        await reporter.ReportAsync("162003", success: true, "всё снято", sessionSecret: null);

        Assert.False(ловушка.Запрос!.Headers.Contains(HubRoutes.SessionSecretHeader));
    }

    [Fact]
    public async Task Сетевой_сбой_не_бросает()
    {
        // Откат уже случился к этому моменту — недоступность hub ничего не должна ломать.
        var reporter = new RevertStatusReporter(
            new HttpClient(new ПадающийHandler()) { BaseAddress = new Uri("http://hub.example.com") });

        var error = await reporter.ReportAsync("162003", true, "всё снято", "секрет");

        Assert.NotNull(error);
    }

    private sealed class ПадающийHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("hub недоступен");
    }
}
