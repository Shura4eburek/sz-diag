using Microsoft.AspNetCore.SignalR;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>SignalR-хаб для агентов. Тонкий слой над сервисами.</summary>
public sealed class AgentHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IKnowledgeBaseScaffolder _kb;
    private readonly IReportStore _reports;
    private readonly ExecCoordinator _exec;
    private readonly PullCoordinator _pull;
    private readonly PushCoordinator _push;

    public AgentHub(SessionRegistry registry, ISessionStore store,
        IKnowledgeBaseScaffolder kb, IReportStore reports, ExecCoordinator exec,
        PullCoordinator pull, PushCoordinator push)
    {
        _registry = registry;
        _store = store;
        _kb = kb;
        _reports = reports;
        _exec = exec;
        _pull = pull;
        _push = push;
    }

    public async Task Register(RegisterRequest request)
    {
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var outcome = _registry.Register(request.Sz, ip, request.Hostname, Context.ConnectionId,
            request.BootTime);
        if (outcome.Rebooted)
        {
            // Пишем в SQLite сразу: in-memory реестр не переживает рестарт hub, а вырубон,
            // о котором никто не узнал, оборачивается ложным «дефект не воспроизведён»
            // (так на 160306 вырубон на нашем же стенде нашли лишь через неделю — п.55).
            var held = outcome.UptimeBefore is { } u ? $", продержалась {u:d\\.hh\\:mm\\:ss}" : "";
            var busy = outcome.ActivityBefore is { } a ? $", активность: {a}" : "";
            Console.WriteLine($"[hub] СЗ {request.Sz}: ВЫРУБОН — клиент перезагрузился " +
                              $"(boot-time {request.BootTime:yyyy-MM-dd HH:mm:ss}){held}{busy}");
            await _store.RecordRebootAsync(new RebootEvent(
                request.Sz, DateTimeOffset.UtcNow, outcome.PreviousBootTime, request.BootTime,
                (long?)outcome.UptimeBefore?.TotalSeconds, outcome.ActivityBefore));
        }
        _kb.EnsureSkeleton(request.Sz);
        await _store.RecordOpenAsync(
            new SessionRecord(request.Sz, ip, request.Hostname, DateTimeOffset.UtcNow, null));
    }

    public Task Heartbeat(string sz)
    {
        _registry.Heartbeat(sz);
        return Task.CompletedTask;
    }

    /// <summary>Агент вернул результат exec — отдаём его ожидающему HTTP-запросу CLI.</summary>
    public Task ExecResult(ExecResult result)
    {
        _exec.Complete(result);
        return Task.CompletedTask;
    }

    /// <summary>Агент отчитался о доставке инструмента.</summary>
    public Task PushResult(PushResult result)
    {
        _push.Complete(result);
        return Task.CompletedTask;
    }

    /// <summary>Агент прислал кусок забираемого файла.</summary>
    public Task PullChunk(PullChunk chunk)
    {
        _pull.AcceptChunk(chunk);
        return Task.CompletedTask;
    }

    /// <summary>Агент закончил забор — отдаём итог ожидающему запросу CLI.</summary>
    public Task PullResult(PullResult result)
    {
        _pull.Complete(result);
        return Task.CompletedTask;
    }

    public Task ReportActivity(string sz, string activity, DateTimeOffset? since)
    {
        _registry.SetActivity(sz, activity, since);
        return Task.CompletedTask;
    }

    public Task UploadReportFile(UploadReportPart part)
    {
        _reports.Save(part.Sz, part.Timestamp, part.FileName, part.Content);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.MarkOfflineByConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
