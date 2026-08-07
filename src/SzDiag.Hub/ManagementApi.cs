using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

public static class ManagementApi
{
    public const string TokenHeader = "X-SzDiag-Mgmt-Token";

    public static void MapManagementApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            var opts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<HubOptions>>().Value;
            var provided = ctx.HttpContext.Request.Headers[TokenHeader].ToString();
            if (string.IsNullOrEmpty(opts.ManagementToken) || provided != opts.ManagementToken)
                return Results.Unauthorized();
            return await next(ctx);
        });

        group.MapGet("/sessions", (SessionRegistry reg) => Results.Ok(reg.GetActive()));

        group.MapPost("/sessions/{sz}/close", async (string sz, SessionCloser closer) =>
            await closer.CloseAsync(sz) ? Results.Ok() : Results.NotFound());

        group.MapPost("/sessions/{sz}/test", async (string sz, string? filter, TestRunTrigger trigger) =>
            await trigger.TriggerAsync(sz, filter) ? Results.Ok() : Results.NotFound());

        group.MapPost("/sessions/{sz}/diag", async (string sz, string? sections, DiagRunTrigger trigger) =>
            await trigger.TriggerAsync(sz, sections) ? Results.Ok() : Results.NotFound());

        // exec: синхронный запуск скрипта на агенте. 404 — СЗ не онлайн, 504 — агент молчит.
        group.MapPost("/sessions/{sz}/exec", async (string sz, ExecCommandRequest body, ExecCoordinator exec) =>
        {
            if (string.IsNullOrWhiteSpace(body.Script)) return Results.BadRequest("пустой скрипт");
            try
            {
                var result = await exec.RunAsync(sz, body.Script, body.TimeoutSeconds,
                    detached: body.Detached);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // Состояние фоновой задачи: короткий запрос, проходит даже под полной нагрузкой,
        // когда обычный exec уже не проходит (бэклог п.43/п.46).
        group.MapGet("/sessions/{sz}/exec/{jobId}", async (string sz, string jobId, int? tail,
            ExecCoordinator exec) =>
        {
            try
            {
                var status = await exec.StatusAsync(sz, jobId, tail ?? ExecLimits.DefaultTailLines);
                return status is null ? Results.NotFound() : Results.Ok(status);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // push: доставить инструмент на клиента (агент качает его с hub сам).
        // Отдаём и каталог раздачи: без него пустой список выглядит как «инструментов нет»,
        // хотя на деле hub смотрит не туда (бэклог п.67).
        group.MapGet("/tools", (ToolCatalog catalog) => Results.Ok(
            new ToolCatalogInfo(catalog.Root, Directory.Exists(catalog.Root), catalog.List())));

        group.MapPost("/sessions/{sz}/push", async (string sz, PushCommandRequest body, PushCoordinator push) =>
        {
            if (string.IsNullOrWhiteSpace(body.Tool)) return Results.BadRequest("не указан инструмент");
            try
            {
                var result = await push.PushAsync(sz, body.Tool);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // pull: забрать файл(ы) с клиента на хост. 404 — СЗ не онлайн, 504 — агент не закончил.
        group.MapPost("/sessions/{sz}/pull", async (string sz, PullCommandRequest body, PullCoordinator pull) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest("пустой путь");
            try
            {
                var result = await pull.PullAsync(sz, body.Path, body.MaxBytes, body.Recurse);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // Таймлайн вырубонов по СЗ: живёт в SQLite и переживает рестарт hub.
        group.MapGet("/sessions/{sz}/reboots", async (string sz, ISessionStore store) =>
            Results.Ok(await store.GetRebootsAsync(sz)));

        // Окна ручных работ: событие питания внутри окна — не дефект, а «гасили руками»
        // (бэклог п.100). Ставится в том числе задним числом.
        group.MapPost("/sessions/{sz}/maintenance", async (string sz, MaintenanceWindow body,
            ISessionStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest("нужна причина");
            if (body.Until < body.From) return Results.BadRequest("конец окна раньше начала");
            await store.AddMaintenanceAsync(body with { Sz = sz });
            return Results.Ok();
        });

        group.MapGet("/sessions/{sz}/maintenance", async (string sz, ISessionStore store) =>
            Results.Ok(await store.GetMaintenanceAsync(sz)));

        group.MapGet("/sessions/{sz}/target", (string sz, SessionRegistry reg, IOptions<HubOptions> opts) =>
        {
            var s = reg.GetActive().FirstOrDefault(x => x.Sz == sz);
            if (s is null) return Results.NotFound();
            var user = opts.Value.ServiceAccount;
            return Results.Ok(new TargetInfo(sz, s.Ip, user, $"ssh {user}@{s.Ip}"));
        });
    }
}
