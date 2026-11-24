// Указатель hub (свой сервис Railway оператора): публичный адрес, по
// которому клиент узнаёт, где сейчас hub и какой релиз агента актуален. Сервис ничего не
// подписывает сам — только проверяет подпись ключом флота, хранит и отдаёт конверты.
// Контракт: specs/002-domain-independent-access/contracts/signpost-api.md.
//
// Env: SZDIAG_SIGN_PUBKEY (SPKI PEM), DATA_DIR (volume, по умолчанию /data), PORT (Railway).
// SZDIAG_FLEET_BUNDLE в той же службе лежит шифротекстом и сервисом не читается.
using SzDiag.Contracts;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;
using SzDiag.Signpost;

var builder = WebApplication.CreateBuilder(args);

// Railway отдаёт порт переменной PORT; локально — 8080.
var port = builder.Configuration["PORT"] ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = PackageStore.MaxPackageBytes + 1);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new LeaseTable(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp =>
    new PackageStore(sp.GetRequiredService<IConfiguration>()["DATA_DIR"] is { Length: > 0 } d ? d : "/data"));
builder.Services.AddSingleton(sp =>
{
    var pem = sp.GetRequiredService<IConfiguration>()["SZDIAG_SIGN_PUBKEY"];
    // Railway хранит многострочное значение как есть, но при вставке руками бывает «\n» строкой.
    pem = pem?.Replace("\\n", "\n");
    return new SignpostKeys(string.IsNullOrWhiteSpace(pem) ? null : new EnvelopeVerifier(pem));
});

var app = builder.Build();

app.MapGet(SignpostRoutes.Health, () => Results.Text("ok"));

app.MapGet(SignpostRoutes.Signpost, (LeaseTable lease, PackageStore store) =>
    Results.Json(new SignpostSnapshot(lease.CurrentEnvelope, store.LoadRelease(), lease.Current), SignpostJson.Options));

app.MapPut(SignpostRoutes.Hub, async (HttpRequest req, SignpostKeys keys, LeaseTable lease) =>
{
    if (keys.Verifier is null) return NoKey();
    var env = await ReadEnvelopeAsync(req);
    var failure = keys.Verifier.Open<HubRecord>(env, out var record);
    if (failure != EnvelopeFailure.None) return Results.BadRequest(new { error = $"подпись: {failure}" });
    if (record!.Kind != SignpostKinds.Hub || string.IsNullOrWhiteSpace(record.HubId))
        return Results.BadRequest(new { error = "ожидалась запись hub" });

    var result = lease.Publish(record, env!);
    return result.Outcome switch
    {
        LeaseOutcome.Accepted => Results.Json(result.Lease, SignpostJson.Options),
        LeaseOutcome.Conflict => Results.Json(result.Lease, SignpostJson.Options, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = "seq не вырос", lease = result.Lease }, SignpostJson.Options,
            statusCode: StatusCodes.Status412PreconditionFailed),
    };
});

app.MapPut(SignpostRoutes.Release, async (HttpRequest req, SignpostKeys keys, PackageStore store) =>
{
    if (keys.Verifier is null) return NoKey();
    var env = await ReadEnvelopeAsync(req);
    var failure = keys.Verifier.Open<ReleaseRecord>(env, out var record);
    if (failure != EnvelopeFailure.None) return Results.BadRequest(new { error = $"подпись: {failure}" });
    if (record!.Kind != SignpostKinds.Release) return Results.BadRequest(new { error = "ожидалась запись release" });
    if (!store.Has(record.PackageSha256))
        return Results.BadRequest(new { error = "сначала загрузи пакет: PUT /package/{sha256}" });

    // Сравнение seq — по уже лежащему релизу, подпись которого проверялась при приёме.
    var current = store.LoadRelease();
    if (current is not null && keys.Verifier.Open<ReleaseRecord>(current, out var prev) == EnvelopeFailure.None
        && record.Seq <= prev!.Seq)
        return Results.Json(new { error = "seq не вырос" }, statusCode: StatusCodes.Status412PreconditionFailed);

    store.SaveRelease(env!);
    return Results.Ok();
});

// Пакет принимается только вместе с подписанным релизом на этот же sha256 (заголовок
// X-SzDiag-Release): иначе любой залил бы пакеты по 100 МБ, забил volume и вытеснил текущий.
// Сверка sha256 тела с путём не даёт подложить чужие байты под подписанный хэш.
app.MapPut(SignpostRoutes.Package, async (string sha256, HttpRequest req, SignpostKeys keys, PackageStore store,
    CancellationToken ct) =>
{
    if (keys.Verifier is null) return NoKey();
    sha256 = sha256.ToLowerInvariant();

    SignedEnvelope? relEnv = null;
    try
    {
        var header = req.Headers[SignpostRoutes.ReleaseHeader].ToString();
        if (header.Length > 0) relEnv = SignedEnvelope.TryParse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header)));
    }
    catch (FormatException) { }
    if (keys.Verifier.Open<ReleaseRecord>(relEnv, out var rel) != EnvelopeFailure.None
        || rel!.Kind != SignpostKinds.Release || rel.PackageSha256 != sha256)
        return Results.Json(new { error = $"нужен подписанный релиз на этот sha256 в заголовке {SignpostRoutes.ReleaseHeader}" },
            statusCode: StatusCodes.Status401Unauthorized);

    string? current = null;
    if (store.LoadRelease() is { } curEnv && keys.Verifier.Open<ReleaseRecord>(curEnv, out var cur) == EnvelopeFailure.None)
        current = cur!.PackageSha256;
    var (ok, error) = await store.SaveAsync(sha256, req.Body, ct, current);
    return ok ? Results.StatusCode(StatusCodes.Status201Created) : Results.BadRequest(new { error });
});

app.MapGet(SignpostRoutes.Package, (string sha256, PackageStore store) =>
{
    sha256 = sha256.ToLowerInvariant();
    return store.Has(sha256)
        ? Results.File(store.PathFor(sha256), "application/zip", "package.zip")
        : Results.NotFound();
});

app.Run();

static async Task<SignedEnvelope?> ReadEnvelopeAsync(HttpRequest req)
{
    using var reader = new StreamReader(req.Body);
    return SignedEnvelope.TryParse(await reader.ReadToEndAsync());
}

static IResult NoKey() => Results.Json(new { error = "SZDIAG_SIGN_PUBKEY не задан — запись выключена" },
    statusCode: StatusCodes.Status503ServiceUnavailable);

public partial class Program;
