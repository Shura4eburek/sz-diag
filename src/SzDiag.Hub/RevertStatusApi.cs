using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Приёмка итога `agent.exe --revert` вне SignalR-сессии (watchdog, headless-откат
/// после ребута) — там нет живого коннекта, чтобы ответить обычным путём. Тот же токен и тот
/// же префикс `/agent`, что у апдейтера (<see cref="AgentPackageApi"/>).
///
/// Боль (бэклог п.59, СЗ 160705): watchdog сработал, `--revert` упал на середине исключения —
/// доступ (sshd, учётка, фаервол) остался на клиенте, а hub ни разу об этом не узнал:
/// `szcli list` продолжал показывать СЗ так, будто всё в порядке.</summary>
public static class RevertStatusApi
{
    public static void MapRevertStatusApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(HubRoutes.AgentApiPrefix).AddEndpointFilter(async (ctx, next) =>
        {
            var opts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<HubOptions>>().Value;
            var provided = ctx.HttpContext.Request.Headers[HubRoutes.TokenHeader].ToString();
            if (string.IsNullOrEmpty(opts.AgentToken) || provided != opts.AgentToken)
                return Results.Unauthorized();
            return await next(ctx);
        });

        group.MapPost("/revert-status", (RevertStatusReport report, SessionRegistry registry, JournalWriter journal) =>
        {
            if (string.IsNullOrWhiteSpace(report.Sz)) return Results.BadRequest("пустой номер СЗ");

            registry.MarkRevertOutcome(report.Sz, report.Success, report.Summary);
            journal.Command(report.Sz, report.Success
                ? "`agent --revert` (watchdog/headless) — откат выполнен чисто"
                : $"`agent --revert` (watchdog/headless) — ОТКАТ НЕ ЗАВЕРШЁН: {report.Summary}");
            return Results.Ok();
        });
    }
}
