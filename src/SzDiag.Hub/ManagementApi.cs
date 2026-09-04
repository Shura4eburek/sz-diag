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

        // Версия/дата сборки hub — `szcli --version` печатает рядом со своей, чтобы
        // рассинхрон («cli свежий, hub протух неделю назад») был виден сразу (бэклог п.165).
        group.MapGet("/version", () => Results.Text(HubBuildInfo.Describe()));

        group.MapPost("/sessions/{sz}/close", async (string sz, SessionCloser closer,
            JournalWriter journal) =>
        {
            var outcome = await closer.CloseAsync(sz);
            if (!outcome.Closed) return Results.NotFound();
            journal.Command(sz, "`close` — доступ згорнуто, сесію закрито");
            return Results.Ok(outcome);
        });

        // Заметку принимаем даже когда сессии нет: мастер отходит от машины, агент может быть
        // уже offline или СЗ закрыта, а зафиксировать физический шаг надо в момент, когда он
        // сделан — иначе информация теряется вместе с сессией (СЗ 160697).
        group.MapPost("/sessions/{sz}/journal", (string sz, JournalNoteRequest body,
            JournalWriter journal) =>
        {
            if (!SzNumber.IsValid(sz)) return Results.BadRequest(SzNumber.Explain(sz));
            if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest("пустая заметка");
            journal.Manual(sz, body.Text.Trim());
            return Results.Ok();
        });

        // Метка конфигурации обязательна: см. TestRunRequest. Без неё прогон не стартует —
        // это дешевле, чем через неделю гадать, на профиле гнали или на стоке.
        group.MapPost("/sessions/{sz}/test", async (string sz, TestRunRequest body,
            TestRunTrigger trigger, ISessionStore store, JournalWriter journal) =>
        {
            var last = await store.GetLastTestConfigAsync(sz);
            var config = body.Config?.Trim();
            if (string.IsNullOrWhiteSpace(config) && body.SameConfig) config = last;

            if (string.IsNullOrWhiteSpace(config))
            {
                var hint = last is null
                    ? "укажите конфигурацию: --config \"EXPO 6000, штатный БП\""
                    : $"прошлый прогон: «{last}» — повторить ту же: --same-config";
                return Results.BadRequest($"прогон без метки конфигурации не запускается; {hint}");
            }

            // Профиль расписания OCCT валидируется здесь, а не молча уезжает на агента с
            // незнакомым именем (бэклог п.124/#60) — известные значения см. OcctScheduleProfiles.
            if (!string.IsNullOrWhiteSpace(body.Schedule) && OcctScheduleProfiles.ResolveFileName(body.Schedule) is null)
                return Results.BadRequest(
                    $"неизвестный профиль расписания «{body.Schedule}»; известны: {string.Join(", ", OcctScheduleProfiles.KnownProfiles)}");

            if (!await trigger.TriggerAsync(sz, body.Filter, body.Schedule)) return Results.NotFound();

            await store.SetLastTestConfigAsync(sz, config);
            var scheduleNote = string.IsNullOrWhiteSpace(body.Schedule) ? "" : $"; розклад OCCT: **{body.Schedule}**";
            journal.Command(sz, $"`test run {body.Filter ?? "усе"}` — старт; конфігурація: **{config}**{scheduleNote}");
            return Results.Ok();
        });

        group.MapPost("/sessions/{sz}/diag", async (string sz, string? sections,
            DiagRunTrigger trigger, JournalWriter journal) =>
        {
            if (!await trigger.TriggerAsync(sz, sections)) return Results.NotFound();
            journal.Command(sz, $"`diag run` — старт (секції: {sections ?? "усі"})");
            return Results.Ok();
        });

        // agent/restart: отдельный от exec путь (бэклог п.202/п.215) — раньше `agent restart`
        // сам ходил через exec-канал и был бесполезен ровно тогда, когда нужен (канал забит).
        // Fire-and-forget: подтверждения ждать нечем, агент себя не убивает сам.
        group.MapPost("/sessions/{sz}/agent/restart", async (string sz,
            RestartAgentTrigger trigger, JournalWriter journal) =>
        {
            if (!await trigger.TriggerAsync(sz)) return Results.NotFound();
            journal.Command(sz, "`agent restart` — перезапуск поставлен (мимо exec-канала)");
            return Results.Ok();
        });

        // exec: синхронный запуск скрипта на агенте. 404 — СЗ не онлайн, 504 — агент молчит.
        group.MapPost("/sessions/{sz}/exec", async (string sz, ExecCommandRequest body,
            ExecCoordinator exec, JournalWriter journal) =>
        {
            if (string.IsNullOrWhiteSpace(body.Script)) return Results.BadRequest("пустой скрипт");
            try
            {
                var result = await exec.RunAsync(sz, body.Script, body.TimeoutSeconds,
                    detached: body.Detached, isolated: body.Isolated, asSystem: body.AsSystem);
                if (result is null) return Results.NotFound();
                var mode = body.Detached ? (body.Isolated ? ", detached+isolated" : ", detached")
                    : (body.AsSystem ? ", as-system" : "");
                journal.Command(sz, $"`exec` — скрипт виконано ({body.Script.Length} символів{mode})");
                return Results.Ok(result);
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

        // Отмена фоновой задачи: тем же коротким каналом, что и статус — он единственный
        // проходит под полной нагрузкой, когда отменить задачу нужнее всего (п.134/172/176).
        group.MapDelete("/sessions/{sz}/exec/{jobId}", async (string sz, string jobId,
            ExecCoordinator exec, JournalWriter journal) =>
        {
            try
            {
                var status = await exec.StatusAsync(sz, jobId, tailLines: 10, cancel: true);
                if (status is null) return Results.NotFound();
                if (status.Cancelled) journal.Command(sz, $"`exec --cancel {jobId}` — фонову задачу знято");
                return Results.Ok(status);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // Список фоновых задач на агенте (jobId «*» — соглашение канала статуса).
        group.MapGet("/sessions/{sz}/exec", async (string sz, ExecCoordinator exec) =>
        {
            try
            {
                var status = await exec.StatusAsync(sz, "*", tailLines: 0);
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

        // План расписания OCCT из раздачи, а не из репозитория (бэклог п.124/#60, СЗ 161346):
        // `deploy/occt/*.json` в репо и `Hub.ToolsRoot/occt/*.json` на боксе молча расходились
        // (5+5 минут против заявленных 90+90) — печатать план имеет смысл только по тому, что
        // реально уедет клиенту.
        group.MapGet("/occt/schedule", (string? profile, ToolCatalog catalog) =>
        {
            var fileName = OcctScheduleProfiles.ResolveFileName(profile);
            if (fileName is null)
                return Results.BadRequest(
                    $"неизвестный профиль «{profile}»; известны: {string.Join(", ", OcctScheduleProfiles.KnownProfiles)}");
            var path = Path.Combine(catalog.Root, "occt", fileName);
            if (!File.Exists(path)) return Results.NotFound($"файла расписания нет в раздаче: {path}");
            var periods = OcctSchedule.TryParsePeriods(File.ReadAllText(path));
            if (periods is null) return Results.UnprocessableEntity($"'{fileName}' не похож на расписание OCCT (нет Periods)");
            return Results.Ok(OcctSchedulePlan.From(periods));
        });

        // Разбор occt-report.html последнего прогона (бэклог п.124/#60): errors/wheaErrors/
        // executedDuration по каждому периоду вместо ручной распаковки gzip из HTML.
        group.MapGet("/sessions/{sz}/test-result", (string sz, IOptions<HubOptions> hubOpts) =>
        {
            var path = TestResultFinder.FindLatestArtifact(sz, "occt-report.html",
                hubOpts.Value.KnowledgeBaseRoot, hubOpts.Value.PullRoot);
            if (path is null) return Results.NotFound($"occt-report.html для СЗ {sz} не найден");
            var summary = OcctReportParser.TryParse(File.ReadAllText(path));
            if (summary is null) return Results.UnprocessableEntity("occt-report.html не распознан этим парсером");
            return Results.Ok(summary);
        });

        group.MapPost("/sessions/{sz}/push", async (string sz, PushCommandRequest body,
            PushCoordinator push, JournalWriter journal) =>
        {
            if (string.IsNullOrWhiteSpace(body.Tool)) return Results.BadRequest("не указан инструмент");
            try
            {
                var result = await push.PushAsync(sz, body.Tool);
                if (result is null) return Results.NotFound();
                journal.Command(sz, $"`push {body.Tool}` — доставка інструмента");
                return Results.Ok(result);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // pull: забрать файл(ы) с клиента на хост. 404 — СЗ не онлайн, 504 — агент не закончил.
        group.MapPost("/sessions/{sz}/pull", async (string sz, PullCommandRequest body,
            PullCoordinator pull, JournalWriter journal) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest("пустой путь");
            try
            {
                var result = await pull.PullAsync(sz, body.Path, body.MaxBytes, body.Recurse, body.Label);
                if (result is null) return Results.NotFound();
                journal.Command(sz, $"`pull {body.Path}` — забір файлів");
                return Results.Ok(result);
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
            ISessionStore store, JournalWriter journal) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest("нужна причина");
            if (body.Until < body.From) return Results.BadRequest("конец окна раньше начала");
            await store.AddMaintenanceAsync(body with { Sz = sz });
            journal.Command(sz, $"`maintenance` — вікно обслуговування " +
                $"{body.From.ToLocalTime():dd.MM HH:mm}–{body.Until.ToLocalTime():dd.MM HH:mm}: {body.Reason}");
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
