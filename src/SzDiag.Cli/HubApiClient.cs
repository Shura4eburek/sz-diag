using System.Net;
using System.Net.Http.Json;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public sealed class HubApiClient : IHubApiClient
{
    private readonly HttpClient _http;

    public HubApiClient(HttpClient http, string managementToken)
    {
        _http = http;
        if (!string.IsNullOrEmpty(managementToken))
            _http.DefaultRequestHeaders.Add("X-SzDiag-Mgmt-Token", managementToken);
    }

    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<SessionInfo>>("/api/sessions", ct) ?? new();

    public async Task<bool> CloseAsync(string sz, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/sessions/{sz}/close", null, ct);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    public async Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/sessions/{sz}/target", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TargetInfo>(ct);
    }

    public async Task<bool> TriggerTestAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(filter)
            ? $"/api/sessions/{sz}/test"
            : $"/api/sessions/{sz}/test?filter={Uri.EscapeDataString(filter)}";
        var resp = await _http.PostAsync(url, null, ct);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    public async Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(sections)
            ? $"/api/sessions/{sz}/diag"
            : $"/api/sessions/{sz}/diag?sections={Uri.EscapeDataString(sections)}";
        var resp = await _http.PostAsync(url, null, ct);
        return resp.StatusCode == HttpStatusCode.OK;
    }
}
