// Прокси к API встроенного контроллера zerotier-one (T056, specs/002-domain-independent-access).
// zerotier-one слушает API только на 127.0.0.1:9993 с ключом из authtoken.secret; наружу
// выходит этот сервис: Bearer-токен hub (ZT_PROXY_TOKEN) + белый список путей (ZtRoutes).
// Клиентам сюда ходить незачем и нечем — токена контроллера у них нет (FR-023).
using System.Security.Cryptography;
using System.Text;
using SzDiag.ZtController;

var builder = WebApplication.CreateSlimBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port)) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var token = builder.Configuration["ZT_PROXY_TOKEN"] ?? "";
var ztHome = builder.Configuration["ZT_HOME"] ?? "/var/lib/zerotier-one";
var ztLocal = builder.Configuration["ZT_LOCAL_API"] ?? "http://127.0.0.1:9993";

builder.Services.AddHttpClient("zt", c =>
{
    c.BaseAddress = new Uri(ztLocal);
    c.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

// Короткий токен = открытая дверь к сети: лучше не стартовать, чем стартовать так.
if (token.Length < 32)
{
    app.Logger.LogCritical("ZT_PROXY_TOKEN не задан или короче 32 символов — прокси не стартует");
    return 3;
}
var tokenBytes = Encoding.UTF8.GetBytes(token);

// Без авторизации — только здоровье: жив ли zerotier-one под прокси.
app.MapGet("/healthz", async (IHttpClientFactory f) =>
{
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/status");
        req.Headers.Add("X-ZT1-Auth", ReadAuthToken(ztHome));
        using var resp = await f.CreateClient("zt").SendAsync(req);
        return resp.IsSuccessStatusCode ? Results.Text("ok") : Results.Text("zerotier-one: " + (int)resp.StatusCode, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Text("zerotier-one недоступен: " + ex.GetType().Name, statusCode: 503);
    }
});

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/healthz") { await next(); return; }
    var auth = ctx.Request.Headers.Authorization.ToString();
    var given = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(auth[7..]) : [];
    if (!CryptographicOperations.FixedTimeEquals(given, tokenBytes))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

// Бэкап identity контроллера в бандл флота: потеря volume без него = новый ID сети.
app.MapGet("/admin/identity", () =>
{
    var path = Path.Combine(ztHome, "identity.secret");
    return File.Exists(path) ? Results.Text(File.ReadAllText(path).Trim()) : Results.NotFound();
});

app.Map("/{**rest}", async (HttpContext ctx, IHttpClientFactory f) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (!ZtRoutes.IsAllowed(ctx.Request.Method, path))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), path);
    req.Headers.Add("X-ZT1-Auth", ReadAuthToken(ztHome));
    if (HttpMethods.IsPost(ctx.Request.Method))
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        req.Content = new ByteArrayContent(ms.ToArray());
        req.Content.Headers.ContentType = new("application/json");
    }
    using var resp = await f.CreateClient("zt").SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    return Results.Content(body, "application/json", Encoding.UTF8, (int)resp.StatusCode);
});

app.Run();
return 0;

// authtoken.secret появляется после первого старта zerotier-one — читаем при каждом запросе.
static string ReadAuthToken(string ztHome) =>
    File.ReadAllText(Path.Combine(ztHome, "authtoken.secret")).Trim();

public partial class Program;
