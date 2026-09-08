using System.Net.Http.Json;
using System.Text.Json;

namespace SzDiag.Erp;

/// <summary>Тонкий транспорт к локальному API: один POST на вызов инструмента.</summary>
public sealed class ErpApiClient : IDisposable
{
    // Имя заголовка задаёт сторона API: с версии клиента от 2026-09-03 это X-TeleAuto-Token,
    // старое X-Api-Token отвечает 401 (поймано на 161642 — fetch падал «неверный токен»).
    private const string TokenHeader = "X-TeleAuto-Token";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ErpApiClient(HttpClient http, string token, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _http.DefaultRequestHeaders.Remove(TokenHeader);
        _http.DefaultRequestHeaders.Add(TokenHeader, token);
    }

    /// <summary>Собирает клиента по конфигу, читая токен из файла.</summary>
    public static ErpApiClient Create(ErpOptions options)
    {
        if (!options.IsConfigured)
            throw new ErpApiException("unavailable", "адрес API не задан в конфиге (секция Erp).");

        var tokenFile = options.ResolveTokenFile();
        if (!File.Exists(tokenFile))
            throw new ErpApiException("no_token", $"нет файла токена: {tokenFile}");

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        return new ErpApiClient(http, File.ReadAllText(tokenFile).Trim(), ownsHttp: true);
    }

    /// <summary>Живость проверяется без токена: сервис может быть поднят, а токен протух.</summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/healthz", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task<JsonElement> CallAsync(string name, object? args = null, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            // Только StringContent: у него известна длина, поэтому уходит Content-Length.
            // PostAsJsonAsync отдаёт JsonContent без длины → HttpClient шлёт chunked, а сервер
            // на той стороне (BaseHTTPRequestHandler) читает РОВНО Content-Length и получает
            // пустое тело — ответ «нет поля name» при корректном запросе (поймано на 161642).
            var payload = JsonSerializer.Serialize(new { name, arguments = args ?? new { } });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            response = await _http.PostAsync("/call", content, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ErpApiException("unavailable", $"сервис не отвечает: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ErpApiException("timeout", "вызов не уложился в таймаут.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ToException(body);

        // Тело успеха всегда объект с полем result; иначе на той стороне что-то сломалось.
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result))
            throw new ErpApiException("internal", "в ответе нет поля result.");

        // Clone обязателен: документ освобождается здесь же, а элемент уезжает наружу.
        return result.Clone();
    }

    /// <summary>Тело ошибки может оказаться не-JSON (упавший сервер отдаёт html) — не падаем на разборе.</summary>
    private static ErpApiException ToException(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal" : "internal";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return new ErpApiException(code, message);
        }
        catch (JsonException)
        {
            return new ErpApiException("internal", "нечитаемый ответ сервиса.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
