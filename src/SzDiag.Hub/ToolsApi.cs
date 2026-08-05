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

        group.MapGet("/{tool}/manifest", (string tool, ToolCatalog catalog) =>
        {
            var manifest = catalog.Manifest(tool);
            return manifest is null ? Results.NotFound() : Results.Ok(manifest);
        });

        group.MapGet("/{tool}/file", (string tool, string path, ToolCatalog catalog) =>
        {
            var full = catalog.ResolveFile(tool, path);
            // 404 и на «нет файла», и на попытку выйти за папку инструмента: подсказывать,
            // что путь существует, но запрещён, незачем.
            return full is null
                ? Results.NotFound()
                : Results.File(full, "application/octet-stream", Path.GetFileName(full));
        });
    }
}
