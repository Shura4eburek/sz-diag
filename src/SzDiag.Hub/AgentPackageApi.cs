using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Раздача пакета агента апдейтеру (SzDiag.Updater). Файлы берутся из
/// HubOptions.AgentDistRoot (кладёт build-dist). Аутентификация — тот же AgentToken,
/// что у агента (заголовок HubRoutes.TokenHeader).</summary>
public static class AgentPackageApi
{
    public static void MapAgentPackageApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(HubRoutes.AgentApiPrefix).AddEndpointFilter(async (ctx, next) =>
        {
            var opts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<HubOptions>>().Value;
            var provided = ctx.HttpContext.Request.Headers[HubRoutes.TokenHeader].ToString();
            if (string.IsNullOrEmpty(opts.AgentToken) || provided != opts.AgentToken)
                return Results.Unauthorized();
            return await next(ctx);
        });

        group.MapGet("/version", (IOptions<HubOptions> opts) =>
        {
            var path = Path.Combine(opts.Value.AgentDistRoot, "version.txt");
            return File.Exists(path)
                ? Results.Text(File.ReadAllText(path).Trim(), "text/plain")
                : Results.NotFound();
        });

        group.MapGet("/package", (IOptions<HubOptions> opts) =>
        {
            var path = Path.Combine(opts.Value.AgentDistRoot, "package.zip");
            return File.Exists(path)
                ? Results.File(path, "application/zip", "package.zip")
                : Results.NotFound();
        });

        group.MapGet("/package.sha256", (IOptions<HubOptions> opts) =>
        {
            var path = Path.Combine(opts.Value.AgentDistRoot, "package.sha256");
            return File.Exists(path)
                ? Results.Text(File.ReadAllText(path).Trim(), "text/plain")
                : Results.NotFound();
        });
    }
}
