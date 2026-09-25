using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Раздача стресс-инструментов агентам по HTTP — тем же каналом, которым агент уже
/// качает свои обновления. Появилась взамен SMB, который на живых заявках не поднимался
/// ничем (`error 67` при открытом 445) из-за VPN/фаерволов на обеих сторонах (бэклог п.1).
///
/// Аутентификация — тот же `AgentToken`, что у пакета агента.</summary>
public static class ToolsApi
{
    public static void MapToolsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ToolRoutes.Prefix).AddEndpointFilter(async (ctx, next) =>
        {
            var opts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<HubOptions>>().Value;
            var provided = ctx.HttpContext.Request.Headers[HubRoutes.TokenHeader].ToString();
            if (string.IsNullOrEmpty(opts.AgentToken) || provided != opts.AgentToken)
                return Results.Unauthorized();
            return await next(ctx);
        });

        group.MapGet("/list", (ToolCatalog catalog) => Results.Ok(catalog.List()));

        group.MapGet("/{tool}/manifest", (string tool, string? req, ToolCatalog catalog, TransferTracker transfers) =>
        {
            var manifest = catalog.Manifest(tool);
            if (manifest is null) return Results.NotFound();
            if (req is not null) transfers.SetTotal(req, manifest.TotalBytes);
            return Results.Ok(manifest);
        });

        group.MapGet("/{tool}/file", (string tool, string path, string? req, ToolCatalog catalog,
            TransferTracker transfers) =>
        {
            var full = catalog.ResolveFile(tool, path);
            // 404 и на «нет файла», и на попытку выйти за папку инструмента: подсказывать,
            // что путь существует, но запрещён, незачем.
            if (full is null) return Results.NotFound();
            Stream body = OpenToolFile(full);
            if (req is not null) body = new CountingReadStream(body, n => transfers.Add(req, n));
            return Results.File(body, "application/octet-stream", Path.GetFileName(full));
        });
    }

    /// <summary>Асинхронный поток на чтение: синхронный FileStream гоняет ReadAsync через пул
    /// потоков, а 300 МБ OCCT — это тысячи чтений на том самом пуле, который у hub уже
    /// захлёбывался (бэклог п.50). Раньше Results.File(path) отдавал файл сам, без этого.</summary>
    public static Stream OpenToolFile(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
