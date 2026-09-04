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

        group.MapPost("/revert-status", async (RevertStatusReport report, SessionRegistry registry,
            JournalWriter journal, ISessionStore store, HttpContext http, CancellationToken ct) =>
        {
            // Токен /agent/* общий на всех агентов (модель угроз: клиентская машина —
            // наименее доверенное устройство), поэтому Sz из тела нельзя принимать как есть.
            // Без валидации формата произвольная строка (`..\..\..\Users\Public\x`) уезжала
            // прямо в KbPaths.SzDir без санитизации — запись мимо vault (Critical-5, ревью
            // волны 1). Соседний `/api/sessions/{sz}/journal` эту же проверку уже делает.
            if (!SzNumber.IsValid(report.Sz)) return Results.BadRequest(SzNumber.Explain(report.Sz));

            if (!IsAuthorizedForSz(registry, report.Sz, http.Connection.RemoteIpAddress?.ToString()))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Watchdog/headless-откат уходит без живого SignalR-коннекта, поэтому обычный
            // SessionCloser его не увидит — историю закрытия пишем здесь сами (Important-3,
            // ревью волны 1: раньше чистый откат по этому пути не оставлял в SQLite закрытия
            // заявки, и история СЗ получала дыру).
            if (report.Success)
                await store.RecordCloseAsync(report.Sz, DateTimeOffset.UtcNow, ct);

            registry.MarkRevertOutcome(report.Sz, report.Success, report.Summary);
            // Журнал СЗ — на украинском, как весь kb (Important-4, ревью волны 1: этот путь
            // единственный писал по-русски, хотя парная запись в AgentHub.RevertResult уже
            // была на украинском).
            journal.Command(report.Sz, report.Success
                ? "`agent --revert` (watchdog/headless) — відкат виконано повністю"
                : $"`agent --revert` (watchdog/headless) — **ВІДКАТ НЕ ЗАВЕРШЕНО**: {report.Summary}");
            return Results.Ok();
        });
    }

    /// <summary>Единственная доступная здесь привязка «эта СЗ — точно этот агент»: токен
    /// `/agent/*` общий на всех агентов, поэтому Sz из тела сам по себе не доказывает, что
    /// прислал его владелец сессии — заражённый клиент мог бы отчитаться за чужую активную
    /// СЗ и выкинуть её из реестра (DoS по соседним заявкам, Critical-5, ревью волны 1).
    /// IP вызова не меняется при ребуте и должен совпасть с IP, под которым СЗ
    /// зарегистрирована по SignalR. Пропускаем проверку, когда сверять не с чем: сессии уже
    /// нет в реестре (обычный случай — watchdog шлёт репорт как раз потому, что живого
    /// коннекта больше нет) или IP вызова не удалось определить.</summary>
    public static bool IsAuthorizedForSz(SessionRegistry registry, string sz, string? remoteIp)
    {
        var info = registry.TryGetInfo(sz);
        if (info is null || remoteIp is null) return true;
        return info.Ip == remoteIp;
    }
}
