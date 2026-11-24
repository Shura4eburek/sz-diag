using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Член сервисной ZT-сети, как его видит контроллер.</summary>
public sealed record ZtMember(string Id, string Name, DateTimeOffset? CreatedAt, IReadOnlyList<string> IpAssignments);

/// <summary>Что hub делает с членами сервисной сети через прокси своего контроллера (T047).
/// Токен контроллера живёт только здесь — клиенту не уходит никогда (FR-023).</summary>
public interface IZtControllerClient
{
    Task<IReadOnlyList<string>> ListMemberIdsAsync(CancellationToken ct);
    Task<ZtMember?> GetMemberAsync(string memberId, CancellationToken ct);

    /// <summary>true — удалён или его уже не было.</summary>
    Task<bool> DeleteMemberAsync(string memberId, CancellationToken ct);
}

public sealed class ZtControllerClient : IZtControllerClient
{
    private readonly HttpClient _http;
    private readonly string _network;

    public ZtControllerClient(IOptions<HubOptions> options)
        : this(options.Value.ZeroTier, new HttpClient { Timeout = TimeSpan.FromSeconds(15) }) { }

    public ZtControllerClient(HubZeroTierOptions zt, HttpClient http)
    {
        _http = http;
        _network = zt.Network.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(zt.ControllerUrl))
            _http.BaseAddress = new Uri(zt.ControllerUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", zt.ControllerToken);
    }

    private string MembersPath => $"controller/network/{_network}/member";

    public async Task<IReadOnlyList<string>> ListMemberIdsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(MembersPath, ct));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    public async Task<ZtMember?> GetMemberAsync(string memberId, CancellationToken ct)
    {
        using var r = await _http.GetAsync($"{MembersPath}/{memberId}", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement, memberId);
    }

    public static ZtMember Parse(JsonElement e, string fallbackId)
    {
        var id = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : fallbackId;
        var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        DateTimeOffset? created = e.TryGetProperty("creationTime", out var c) && c.TryGetInt64(out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
        var ips = e.TryGetProperty("ipAssignments", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        return new ZtMember(id, name, created, ips);
    }

    public async Task<bool> DeleteMemberAsync(string memberId, CancellationToken ct)
    {
        using var r = await _http.DeleteAsync($"{MembersPath}/{memberId}", ct);
        return r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.NotFound;
    }
}
