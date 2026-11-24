using System.Net.Http.Json;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Contracts;

/// <summary>Откуда и как искать hub / релиз агента (specs/002, contracts/signpost-api.md).</summary>
/// <param name="ConfigHubUrl">Явный HubUrl из конфига — главнее всего.</param>
/// <param name="SignpostUrl">База сервиса указателя — свой Railway оператора.</param>
/// <param name="SignPublicKeys">Публичные ключи флота (PEM). Пусто — указатель не используется:
/// без проверки подписи его ответу верить нельзя.</param>
/// <param name="Modes">Какие режимы адреса hub этот процесс умеет (zt — только агент с ZT-нодой).</param>
public sealed record LocatorOptions(
    string? ConfigHubUrl,
    string? SignpostUrl,
    IReadOnlyList<string> SignPublicKeys,
    IReadOnlyCollection<string> Modes)
{
    public static readonly IReadOnlyCollection<string> DefaultModes = [HubEndpointMode.Lan, HubEndpointMode.Tunnel];

    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(4);
}

public sealed record SourceRejection(string Source, string Reason)
{
    public override string ToString() => $"{Source}: {Reason}";
}

/// <param name="Mode">config / lan-broadcast / lan / tunnel / zt.</param>
/// <param name="Source">Какой источник дал адрес: config, lan, signpost.</param>
public sealed record HubLocation(string Url, string Mode, string Source, IReadOnlyList<SourceRejection> Rejected)
{
    /// <summary>Id сервисной ZT-сети, если hub найден по zt-адресу (через неё и ходить).</summary>
    public string? ZtNetwork { get; init; }
}

/// <param name="PackageUrls">Абсолютные адреса пакета, по порядку.</param>
public sealed record ReleaseLocation(ReleaseRecord Release, string Source, IReadOnlyList<string> PackageUrls,
    IReadOnlyList<SourceRejection> Rejected);

/// <summary>Единый резолвер для апдейтера и агента: config HubUrl → LAN-broadcast → указатель.
/// Каждый отвергнутый источник попадает в <c>Rejected</c> с причиной — в лог клиента, иначе на
/// живой заявке гадаешь, почему апдейтер «не видит hub».</summary>
public sealed class HubLocator
{
    public const string ModeConfig = "config";
    public const string ModeBroadcast = "lan-broadcast";

    private readonly LocatorOptions _opts;
    private readonly HttpClient _http;
    private readonly SeenSeqStore _seen;
    private readonly Func<CancellationToken, Task<string?>> _lanDiscovery;
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly Func<string, string, CancellationToken, Task<bool>>? _ztProbe;

    /// <param name="lanDiscovery">UDP-broadcast; null — без LAN-поиска.</param>
    /// <param name="probe">Проверка, что hub по адресу жив; по умолчанию GET /healthz.</param>
    public HubLocator(LocatorOptions opts, HttpClient http, SeenSeqStore seen,
        Func<CancellationToken, Task<string?>>? lanDiscovery = null,
        Func<string, CancellationToken, Task<bool>>? probe = null,
        Func<string, string, CancellationToken, Task<bool>>? ztProbe = null)
    {
        _opts = opts;
        _http = http;
        _seen = seen;
        _lanDiscovery = lanDiscovery ?? (_ => Task.FromResult<string?>(null));
        _probe = probe ?? ProbeHealthAsync;
        _ztProbe = ztProbe;
    }

    /// <summary>LAN-поиск поверх <see cref="HubDiscovery"/>: «не нашёл» — null, а не исключение.</summary>
    public static Func<CancellationToken, Task<string?>> Broadcast(string agentToken, TimeSpan? timeout = null)
        => async ct =>
        {
            try { return await HubDiscovery.FindHubAsync(agentToken, timeout: timeout, ct: ct); }
            catch (HubNotFoundException) { return null; }
        };

    /// <summary>Причины отказа источников в последнем поиске — и когда ничего не нашлось
    /// (тогда это единственное объяснение «почему» для лога клиента).</summary>
    public IReadOnlyList<SourceRejection> LastRejected { get; private set; } = [];

    public async Task<HubLocation?> LocateAsync(CancellationToken ct = default)
    {
        var rejected = new List<SourceRejection>();
        LastRejected = rejected;
        // Явный адрес из конфига — первый, но не единственный: домен туннеля умер — клиент
        // обязан спросить LAN и указатель, а не застрять на мёртвом адресе (FR-007, SC-001).
        if (!string.IsNullOrWhiteSpace(_opts.ConfigHubUrl))
        {
            var cfg = _opts.ConfigHubUrl.Trim();
            if (await _probe(cfg, ct)) return new HubLocation(cfg, ModeConfig, "config", rejected);
            rejected.Add(new("config", $"{cfg} не отвечает"));
        }

        var lan = await _lanDiscovery(ct);
        if (!string.IsNullOrWhiteSpace(lan))
            return new HubLocation(lan, ModeBroadcast, "lan", rejected);
        rejected.Add(new("lan", "hub не ответил на broadcast"));

        using var verifier = Verifier(rejected);
        if (verifier is null) return null;

        foreach (var (name, fetch) in Sources())
        {
            SignedEnvelope? env;
            try { env = await fetch(SignpostKinds.Hub, ct); }
            catch (Exception ex) when (IsNetwork(ex, ct)) { rejected.Add(new(name, Describe(ex))); continue; }

            if (!TryOpen(verifier, env, SignpostKinds.Hub, name, rejected, out HubRecord? hub)) continue;

            foreach (var ep in hub!.Endpoints)
            {
                if (!_opts.Modes.Contains(ep.Mode))
                {
                    rejected.Add(new($"{name}/{ep.Mode}", "режим не поддерживается этим процессом"));
                    continue;
                }
                if (ep.Mode == HubEndpointMode.Zt)
                {
                    // zt-адрес проверяется только через ноду ZT процесса (агент); обычный сокет ОС
                    // до сервисной сети не дотянется. Contracts о libzt не знает — нода снаружи.
                    if (_ztProbe is null || string.IsNullOrWhiteSpace(hub.ZtNetwork))
                    {
                        rejected.Add(new($"{name}/{ep.Mode}", _ztProbe is null ? "ZeroTier в этом процессе нет" : "в записи нет id сети"));
                        continue;
                    }
                    if (await _ztProbe(hub.ZtNetwork, ep.Url, ct))
                    {
                        _seen.Remember(SignpostKinds.Hub, hub.Seq);
                        return new HubLocation(ep.Url, ep.Mode, name, rejected) { ZtNetwork = hub.ZtNetwork };
                    }
                    rejected.Add(new($"{name}/{ep.Mode}", $"{ep.Url} не отвечает через сеть {hub.ZtNetwork}"));
                    continue;
                }
                if (await _probe(ep.Url, ct))
                {
                    _seen.Remember(SignpostKinds.Hub, hub.Seq);
                    return new HubLocation(ep.Url, ep.Mode, name, rejected);
                }
                rejected.Add(new($"{name}/{ep.Mode}", $"{ep.Url} не отвечает"));
            }
        }
        return null;
    }

    /// <summary>Подписанный релиз агента из указателя. LAN-hub сюда не входит — он раздаёт
    /// пакет по старому пути /agent/* под токеном агента.</summary>
    public async Task<ReleaseLocation?> LocateReleaseAsync(CancellationToken ct = default)
    {
        var rejected = new List<SourceRejection>();
        LastRejected = rejected;
        using var verifier = Verifier(rejected);
        if (verifier is null) return null;

        foreach (var (name, fetch) in Sources())
        {
            SignedEnvelope? env;
            try { env = await fetch(SignpostKinds.Release, ct); }
            catch (Exception ex) when (IsNetwork(ex, ct)) { rejected.Add(new(name, Describe(ex))); continue; }

            if (!TryOpen(verifier, env, SignpostKinds.Release, name, rejected, out ReleaseRecord? rel)) continue;
            if (!IsSha256(rel!.PackageSha256))
            {
                rejected.Add(new(name, "в релизе нет корректного sha256 пакета"));
                continue;
            }

            var urls = rel.PackageUrls
                .Select(u => Absolute(_opts.SignpostUrl, u))
                .Where(u => u is not null)
                .Select(u => u!)
                .ToList();
            if (urls.Count == 0)
            {
                rejected.Add(new(name, "в релизе нет адресов пакета"));
                continue;
            }
            _seen.Remember(SignpostKinds.Release, rel.Seq);
            return new ReleaseLocation(rel, name, urls, rejected);
        }
        return null;
    }

    private EnvelopeVerifier? Verifier(List<SourceRejection> rejected)
    {
        var keys = _opts.SignPublicKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (keys.Count == 0)
        {
            rejected.Add(new("signpost", "в конфиге нет публичного ключа флота (SignPublicKey) — указатель не используется"));
            return null;
        }
        try { return new EnvelopeVerifier(keys); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            rejected.Add(new("signpost", $"публичный ключ флота не разобран: {ex.Message}"));
            return null;
        }
    }

    private bool TryOpen<T>(EnvelopeVerifier verifier, SignedEnvelope? env, string kind, string source,
        List<SourceRejection> rejected, out T? record) where T : class
    {
        record = null;
        if (env is null)
        {
            rejected.Add(new(source, $"нет записи «{kind}»"));
            return false;
        }
        var failure = verifier.Open(env, out record);
        if (failure != EnvelopeFailure.None)
        {
            rejected.Add(new(source, failure switch
            {
                EnvelopeFailure.UnknownKid => "подпись чужим ключом (kid не наш)",
                EnvelopeFailure.BadSignature => "подпись не сошлась — запись подменена",
                _ => "запись не разобрана",
            }));
            record = null;
            return false;
        }

        var (recKind, seq) = record switch
        {
            HubRecord h => (h.Kind, h.Seq),
            ReleaseRecord r => (r.Kind, r.Seq),
            _ => ("", 0L),
        };
        if (recKind != kind)
        {
            rejected.Add(new(source, $"ожидалась запись «{kind}», пришла «{recKind}»"));
            record = null;
            return false;
        }
        var seen = _seen.Get(kind);
        if (seq < seen)
        {
            rejected.Add(new(source, $"запись старее уже принятой (seq {seq} < {seen}) — возможен replay"));
            record = null;
            return false;
        }
        return true;
    }

    // Источник один — свой указатель оператора. Список оставлен: так проще добавить второй
    // указатель, не трогая логику проверки.
    private IEnumerable<(string Name, Func<string, CancellationToken, Task<SignedEnvelope?>> Fetch)> Sources()
    {
        if (!string.IsNullOrWhiteSpace(_opts.SignpostUrl))
            yield return ("signpost", async (kind, ct) =>
            {
                var snap = await FetchSignpostAsync(_opts.SignpostUrl!, ct);
                return kind == SignpostKinds.Hub ? snap.Hub : snap.Release;
            });
    }

    private async Task<SignpostSnapshot> FetchSignpostAsync(string baseUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opts.FetchTimeout);
        return await _http.GetFromJsonAsync<SignpostSnapshot>(Join(baseUrl, SignpostRoutes.Signpost),
                   SignpostJson.Options, cts.Token)
               ?? new SignpostSnapshot(null, null, null);
    }

    private async Task<bool> ProbeHealthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opts.ProbeTimeout);
            using var r = await _http.GetAsync(Join(url, "/healthz"), cts.Token);
            // За Cloudflare Access hub отвечает 302/403 на запрос без service token — но отвечает:
            // туннель жив. Мёртвый туннель даёт 530/502.
            return (int)r.StatusCode < 500;
        }
        catch (Exception ex) when (IsNetwork(ex, ct)) { return false; }
    }

    private static bool IsNetwork(Exception ex, CancellationToken ct)
        => ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException
           || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "таймаут",
        System.Text.Json.JsonException => "ответ не разобран",
        HttpRequestException h when h.StatusCode is not null => $"HTTP {(int)h.StatusCode}",
        _ => ex.Message,
    };

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static string? Absolute(string? baseUrl, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https") return abs.ToString();
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(url) ? null : Join(baseUrl, url);
    }

    private static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
}
