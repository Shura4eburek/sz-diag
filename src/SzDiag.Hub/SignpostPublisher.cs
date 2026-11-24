using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Hub;

/// <summary>Публикует «где сейчас hub» в подписанном указателе и продлевает аренду
/// (specs/002, US1/US4). Hub на ПК или на ноуте — клиенты идут туда, где он запущен. Указатель
/// недоступен — hub работает как раньше (LAN, туннель), публикация повторяется в фоне.</summary>
public sealed class SignpostPublisher : BackgroundService
{
    private readonly HubOptions _hub;
    private readonly HubSignpostOptions _opts;
    private readonly HubStatusTracker _status;
    private readonly ILogger<SignpostPublisher> _logger;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<HubEndpoint>> _endpoints;
    private readonly string _baseDir;

    private EnvelopeSigner? _signer;
    private string _hubId = "";
    private long _lastSeq;
    private bool _takeover;
    private string _lastState = "";

    public SignpostPublisher(IOptions<HubOptions> options, HubStatusTracker status, ILogger<SignpostPublisher> logger)
        : this(options.Value, status, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            TimeProvider.System, null, AppContext.BaseDirectory)
    {
    }

    public SignpostPublisher(HubOptions hub, HubStatusTracker status, ILogger<SignpostPublisher> logger,
        HttpClient http, TimeProvider time, Func<IReadOnlyList<HubEndpoint>>? endpoints, string baseDir)
    {
        _hub = hub;
        _opts = hub.Signpost;
        _status = status;
        _logger = logger;
        _http = http;
        _time = time;
        _baseDir = baseDir;
        _takeover = _opts.Takeover;
        _endpoints = endpoints ?? (() => DefaultEndpoints(_hub));
    }

    public string HubName => string.IsNullOrWhiteSpace(_opts.HubName) ? Environment.MachineName : _opts.HubName;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled || string.IsNullOrWhiteSpace(_opts.Url))
        {
            _status.Signpost(new SignpostStatus(SignpostStates.Off, null, null, null));
            return;
        }
        if (!Init()) return;

        while (!ct.IsCancellationRequested)
        {
            await PublishOnceAsync(ct);
            try { await Task.Delay(_opts.PublishInterval, _time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Загрузить ключ подписи и id машины. false — публиковать нечем (статус уже выставлен).</summary>
    public bool Init()
    {
        var keyPath = Path.IsPathRooted(_opts.SignKeyPath) ? _opts.SignKeyPath : Path.Combine(_baseDir, _opts.SignKeyPath);
        try
        {
            _signer = EnvelopeSigner.FromPrivatePem(File.ReadAllText(keyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            Report(SignpostStates.Misconfigured, null,
                $"ключ подписи флота не загружен ({keyPath}): {ex.Message} — пересобери dist после tools\\fleet.ps1");
            return false;
        }
        _hubId = HubIdentity(_baseDir);
        return true;
    }

    /// <summary>Одна публикация. Отдельно от цикла — для тестов.</summary>
    public async Task PublishOnceAsync(CancellationToken ct)
    {
        if (_signer is null) return;
        var endpoints = _endpoints();
        var now = _time.GetUtcNow();
        _lastSeq = Math.Max(now.ToUnixTimeMilliseconds(), _lastSeq + 1);
        var record = new HubRecord(_hubId, HubName, endpoints, null, _lastSeq, now, _takeover);
        var env = _signer.SignRecord(record);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PutAsync(Url(SignpostRoutes.Hub),
                new StringContent(env.ToJson(), Encoding.UTF8, "application/json"), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Report(SignpostStates.Unreachable, null, $"указатель недоступен: {ex.Message}");
            return;
        }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    // Перехват нужен один раз: дальше это обычное продление своей аренды.
                    _takeover = false;
                    Report(SignpostStates.Published, null, null);
                    break;
                case HttpStatusCode.Conflict:
                    var holder = await ReadLeaseAsync(resp, ct);
                    Report(SignpostStates.OtherHolder, holder,
                        $"указатель держит hub «{holder?.HubName}» до {holder?.ExpiresAt.ToLocalTime():HH:mm:ss} — " +
                        "новые клиенты идут туда; перехват: start-hub.cmd --takeover");
                    break;
                case HttpStatusCode.PreconditionFailed:
                    Report(SignpostStates.Misconfigured, null,
                        "указатель отверг seq как старый — часы этой машины ушли назад?");
                    break;
                case HttpStatusCode.BadRequest:
                    Report(SignpostStates.Misconfigured, null,
                        "указатель отверг подпись: ключ флота этого hub не совпадает с SZDIAG_SIGN_PUBKEY сервиса");
                    break;
                case HttpStatusCode.ServiceUnavailable:
                    Report(SignpostStates.Misconfigured, null, "в указателе не задан SZDIAG_SIGN_PUBKEY (tools\\fleet.ps1 init)");
                    break;
                default:
                    Report(SignpostStates.Unreachable, null, $"указатель ответил HTTP {(int)resp.StatusCode}");
                    break;
            }
        }
    }

    private void Report(string state, LeaseInfo? holder, string? message)
    {
        var prev = _status.SignpostSnapshot();
        var publishedAt = state == SignpostStates.Published ? _time.GetUtcNow() : prev.LastPublishedAt;
        _status.Signpost(new SignpostStatus(state, publishedAt, holder, message));

        // В консоль — только смена состояния: продление каждые 30 с иначе засыплет лог.
        var key = state + "|" + holder?.HolderHubId;
        if (key == _lastState) return;
        _lastState = key;
        if (state == SignpostStates.Published)
            _logger.LogInformation("Указатель: hub «{Name}» опубликован ({Url})", HubName, _opts.Url);
        else
            _logger.LogWarning("Указатель: {Message}", message);
    }

    private static async Task<LeaseInfo?> ReadLeaseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadFromJsonAsync<LeaseInfo>(SignpostJson.Options, ct); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private string Url(string path) => _opts.Url.TrimEnd('/') + path;

    /// <summary>Стабильный id машины-hub: файл рядом с exe. Не секрет — только чтобы аренда
    /// отличала «тот же hub продлевает» от «пришёл другой».</summary>
    public static string HubIdentity(string baseDir)
    {
        var path = Path.Combine(baseDir, "hub-id");
        try
        {
            if (File.Exists(path))
            {
                var id = File.ReadAllText(path).Trim();
                if (id.Length > 0) return id;
            }
            var fresh = $"{Environment.MachineName.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..8]}";
            File.WriteAllText(path, fresh);
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Environment.MachineName.ToLowerInvariant();
        }
    }

    // Виртуальные адаптеры (VPN, ZeroTier, гипервизоры) — не «LAN hub»: адрес на них клиенту
    // ничего не даст, а ZeroTier появится отдельным режимом (US3).
    private static readonly string[] VirtualAdapterHints =
        ["zerotier", "radmin", "vpn", "tap-", "pritunl", "wireguard", "hyper-v", "vmware", "virtualbox", "tailscale", "bluetooth"];

    /// <summary>LAN-адреса hub (частные IPv4 на физических адаптерах со шлюзом) + доп. адреса из конфига.</summary>
    public static IReadOnlyList<HubEndpoint> DefaultEndpoints(HubOptions hub)
    {
        var list = new List<HubEndpoint>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (VirtualAdapterHints.Any(desc.Contains)) continue;
                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                     && !g.Address.Equals(IPAddress.Any)))
                    continue;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ua.Address))
                        list.Add(new HubEndpoint(HubEndpointMode.Lan, $"http://{ua.Address}:{hub.Port}"));
                }
            }
        }
        catch (NetworkInformationException) { }

        list.AddRange(hub.Signpost.ExtraEndpoints
            .Where(e => !string.IsNullOrWhiteSpace(e.Url))
            .Select(e => new HubEndpoint(string.IsNullOrWhiteSpace(e.Mode) ? HubEndpointMode.Tunnel : e.Mode, e.Url.Trim())));
        return list.DistinctBy(e => e.Url).ToList();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public override void Dispose()
    {
        _signer?.Dispose();
        base.Dispose();
    }
}
