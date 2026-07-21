using SzDiag.Contracts;

namespace SzDiag.Updater;

/// <summary>HTTP-клиент раздачи пакета. Токен шлётся в HubRoutes.TokenHeader.</summary>
public sealed class HttpUpdateClient : IUpdateClient
{
    private readonly HttpClient _http;

    public HttpUpdateClient(string hubBaseUrl, string token, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(hubBaseUrl);
        _http.DefaultRequestHeaders.Remove(HubRoutes.TokenHeader);
        _http.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, token);
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync(HubRoutes.AgentVersionRoute, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadAsStringAsync(ct)).Trim();
    }

    public async Task<string> GetPackageSha256Async(CancellationToken ct = default)
    {
        var r = await _http.GetAsync(HubRoutes.AgentPackageSha256Route, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadAsStringAsync(ct)).Trim().ToLowerInvariant();
    }

    public async Task DownloadPackageAsync(string destZipPath, CancellationToken ct = default)
    {
        var r = await _http.GetAsync(HubRoutes.AgentPackageRoute, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        await using var fs = File.Create(destZipPath);
        await r.Content.CopyToAsync(fs, ct);
    }
}
