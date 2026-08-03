using System.Net;
using System.Net.Http.Json;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public sealed class HubApiClient : IHubApiClient
{
    /// <summary>Потолок для быстрых управляющих запросов (список СЗ, close, запуск прогона).
    /// Долгие операции задают свой срок сами — см. <see cref="ExecAsync"/>.</summary>
    private static readonly TimeSpan ShortRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public HubApiClient(HttpClient http, string managementToken)
    {
        _http = http;
        // Дефолтные 100 секунд HttpClient.Timeout срезали длинный exec раньше, чем истекал
        // срок, заданный пользователем через --timeout: 8-гигабайтная закачка на агенте
        // падала с TaskCanceledException на 100-й секунде. Снимаем общий потолок и
        // отмеряем время на каждом вызове отдельно.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(managementToken))
            _http.DefaultRequestHeaders.Add("X-SzDiag-Mgmt-Token", managementToken);
    }

    /// <summary>Токен, отменяющийся через <see cref="ShortRequestTimeout"/> — чтобы снятие
    /// общего HttpClient.Timeout не превращало зависший hub в вечное ожидание.</summary>
    private static CancellationTokenSource Short(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ShortRequestTimeout);
        return cts;
    }

    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default)
    {
        using var cts = Short(ct);
        return await _http.GetFromJsonAsync<List<SessionInfo>>("/api/sessions", cts.Token) ?? new();
    }

    public async Task<bool> CloseAsync(string sz, CancellationToken ct = default)
    {
        using var cts = Short(ct);
        var resp = await _http.PostAsync($"/api/sessions/{sz}/close", null, cts.Token);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    public async Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default)
    {
        using var cts = Short(ct);
        var resp = await _http.GetAsync($"/api/sessions/{sz}/target", cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TargetInfo>(cts.Token);
    }

    public async Task<bool> TriggerTestAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(filter)
            ? $"/api/sessions/{sz}/test"
            : $"/api/sessions/{sz}/test?filter={Uri.EscapeDataString(filter)}";
        using var cts = Short(ct);
        var resp = await _http.PostAsync(url, null, cts.Token);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Выполнить скрипт на агенте и дождаться вывода. null — СЗ не онлайн.</summary>
    /// <exception cref="TimeoutException">Агент не ответил (hub вернул 504).</exception>
    public async Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        // HttpClient.Timeout должен быть больше, чем ждёт hub, иначе клиент отвалится раньше
        // и мы увидим невнятный TaskCanceledException вместо честного 504.
        var wait = (timeoutSeconds ?? ExecLimits.DefaultTimeoutSeconds)
                   + ExecLimits.HubGraceSeconds + 30;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sz}/exec")
        {
            Content = JsonContent.Create(new ExecCommandRequest(script, timeoutSeconds)),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(wait));

        var resp = await _http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode == HttpStatusCode.GatewayTimeout)
            throw new TimeoutException($"агент СЗ {sz} не ответил на exec");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ExecResult>(cancellationToken: cts.Token);
    }

    public async Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(sections)
            ? $"/api/sessions/{sz}/diag"
            : $"/api/sessions/{sz}/diag?sections={Uri.EscapeDataString(sections)}";
        using var cts = Short(ct);
        var resp = await _http.PostAsync(url, null, cts.Token);
        return resp.StatusCode == HttpStatusCode.OK;
    }
}
