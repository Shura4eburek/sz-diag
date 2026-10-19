using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class AgentSessionTests
{
    private sealed class FakeManager : ISystemAccessManager
    {
        public int OpenCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public RevertState Open(AccessSpec spec)
        {
            OpenCalls++;
            return new RevertState { Sz = spec.Sz };
        }
        public RevertOutcome Revert(RevertState state)
        {
            RevertCalls++;
            return new RevertOutcome(new[] { "sshd", "учётка svc-diag" }, Array.Empty<RevertStepFailure>());
        }
        public void Resume(RevertState state, AccessSpec spec) => ResumeCalls++;
        public string? PersistedSecret { get; private set; }
        public void PersistSessionSecret(RevertState state, string? secret)
        {
            PersistedSecret = secret;
            state.SessionSecret = secret;
        }
    }

    private sealed class FakeHubLink : IHubLink
    {
        public bool Connected { get; private set; }
        public string? RegisteredSz { get; private set; }
        public int Heartbeats { get; private set; }
        public bool Disposed { get; private set; }
        private Func<string, Task>? _onRevert;

        public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }
        public DateTimeOffset? RegisteredBootTime { get; private set; }
        public string? SecretToIssue { get; init; }
        public int RegisterCalls { get; private set; }
        public Task<string?> RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null, string? lastShutdown = null, string? agentUser = null, int? agentSessionId = null, CancellationToken ct = default) { RegisteredSz = sz; RegisteredBootTime = bootTime; RegisterCalls++; return Task.FromResult(SecretToIssue); }
        public Task ReportPowerEventsAsync(SzDiag.Contracts.PowerEventsReport report, CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) { Heartbeats++; return Task.CompletedTask; }
        public SzDiag.Contracts.AccessReportRequest? AccessReport { get; private set; }
        public Task ReportAccessAsync(SzDiag.Contracts.AccessReportRequest report, CancellationToken ct = default) { AccessReport = report; AccessReportCount++; return Task.CompletedTask; }
        public void OnRevert(Func<string, Task> handler) => _onRevert = handler;
        private Func<Task>? _onReconnected;
        public void OnReconnected(Func<Task> handler) => _onReconnected = handler;
        /// <summary>Сымитировать восстановление соединения так, как это делает SignalR.</summary>
        public Task RaiseReconnectedAsync() => _onReconnected?.Invoke() ?? Task.CompletedTask;
        public int AccessReportCount { get; private set; }
        public List<SzDiag.Contracts.UploadReportPart> Uploaded { get; } = new();
        private Func<string, string?, string?, Task>? _onRunTests;
        public void OnRunTests(Func<string, string?, string?, Task> handler) => _onRunTests = handler;
        public void OnRunDiag(Func<string, string?, Task> handler) { }
        public Func<SzDiag.Contracts.ExecRequest, Task>? ExecHandler { get; private set; }
        public List<SzDiag.Contracts.ExecResult> ExecResults { get; } = new();
        public void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler) => ExecHandler = handler;
        public Task SendExecResultAsync(SzDiag.Contracts.ExecResult result, CancellationToken ct = default) { ExecResults.Add(result); return Task.CompletedTask; }
        public Task SendExecAckAsync(SzDiag.Contracts.ExecAck ack, CancellationToken ct = default) => Task.CompletedTask;
        public void OnExecStatus(Func<SzDiag.Contracts.ExecStatusRequest, Task> handler) { }
        public Task SendExecJobStatusAsync(SzDiag.Contracts.ExecJobStatus status, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPush(Func<SzDiag.Contracts.PushRequest, Task> handler) { }
        public Task SendPushResultAsync(SzDiag.Contracts.PushResult result, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPull(Func<SzDiag.Contracts.PullRequest, Task> handler) { }
        public Task SendPullAckAsync(SzDiag.Contracts.PullAck ack, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullChunkAsync(SzDiag.Contracts.PullChunk chunk, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullResultAsync(SzDiag.Contracts.PullResult result, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part, CancellationToken ct = default)
        {
            Uploaded.Add(part);
            return Task.CompletedTask;
        }
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
            => Task.CompletedTask;
        public List<SzDiag.Contracts.RevertResult> RevertResults { get; } = new();
        public Task SendRevertResultAsync(SzDiag.Contracts.RevertResult result, CancellationToken ct = default)
        {
            // Отправка ДО DisposeAsync — иначе итог отката теряется вместе с каналом (п.119).
            Assert.False(Disposed);
            RevertResults.Add(result);
            return Task.CompletedTask;
        }
        public void OnRestartAgent(Action<string> handler) { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        public Task FireRevert(string sz) => _onRevert!(sz);
        public Task FireRunTests(string sz, string? filter = null, string? schedule = null) => _onRunTests!(sz, filter, schedule);
    }

    private static AccessSpec Spec() =>
        new("156864", "svc-diag", "ssh-ed25519 AAAA...", 22, TimeSpan.FromHours(6));

    [Fact]
    public async Task StartAsync_OpensAccessConnectsAndRegisters()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.StartAsync();

        Assert.Equal(1, mgr.OpenCalls);
        Assert.True(link.Connected);
        Assert.Equal("156864", link.RegisteredSz);
    }

    // Реконнект обязан перерегистрировать агента (СЗ 162003, бэклог п.273): SignalR после
    // обрыва выдаёт новый ConnectionId, и пока hub его не узнал, exec/push/close уходят на
    // закрытое соединение — снаружи это «heartbeat свежий, агент не отвечает».
    [Fact]
    public async Task Reconnected_ПеререгистрируетАгентаИСообщаетДоступ()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();
        var registersAfterStart = link.RegisterCalls;
        var accessAfterStart = link.AccessReportCount;

        await link.RaiseReconnectedAsync();

        Assert.Equal(registersAfterStart + 1, link.RegisterCalls);
        Assert.Equal(accessAfterStart + 1, link.AccessReportCount);
        Assert.Equal(1, mgr.OpenCalls);   // доступ повторно НЕ открываем: это только адрес
    }

    [Fact]
    public async Task Reconnected_ПослеResume_ТожеПеререгистрирует()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.ResumeAsync(new RevertState { Sz = "156864" });
        var before = link.RegisterCalls;

        await link.RaiseReconnectedAsync();

        Assert.Equal(before + 1, link.RegisterCalls);
    }

    // Секрет обязан осесть в state.json: headless-откат (watchdog, после ребута) идёт без
    // живого SignalR, и доказать hub владение СЗ там больше нечем.
    [Fact]
    public async Task StartAsync_СохраняетСекретСессии()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink { SecretToIssue = "секрет-от-хаба" };
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.StartAsync();

        Assert.Equal("секрет-от-хаба", mgr.PersistedSecret);
    }

    [Fact]
    public async Task ResumeAsync_ОбновляетСекретПослеРебута()
    {
        // Hub мог перезапуститься, пока клиент ребутился: секрет будет новым.
        var mgr = new FakeManager();
        var link = new FakeHubLink { SecretToIssue = "новый-секрет" };
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.ResumeAsync(new RevertState { Sz = "156864", SessionSecret = "старый" });

        Assert.Equal("новый-секрет", mgr.PersistedSecret);
    }

    // Hub перестал угадывать адрес клиента из RemoteIpAddress: без этого отчёта он не знает,
    // чем машина доступна, и `szcli target` не может построить рабочую строку.
    [Fact]
    public async Task StartAsync_СообщаетHubРежимДоступа()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");

        await session.StartAsync();

        Assert.NotNull(link.AccessReport);
        Assert.Equal("156864", link.AccessReport!.Sz);
    }

    [Fact]
    public async Task StartAsync_HubНайденBroadcastом_РежимПрямой()
    {
        // Машина в одной сети с боксом — туннель только замедлит и добавит зависимость.
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1",
            foundHubByBroadcast: true);

        await session.StartAsync();

        Assert.Equal(AccessMode.Direct, link.AccessReport!.AccessMode);
    }

    // После ребута имя quick tunnel'а другое: не отчитаться заново — оставить hub с адресом
    // мёртвого туннеля.
    [Fact]
    public async Task ResumeAsync_СообщаетHubРежимДоступаЗаново()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");

        await session.ResumeAsync(new RevertState
        {
            Sz = "156864",
            StartedQuickTunnel = true,
            QuickTunnelHost = "новое-после-ребута.trycloudflare.com",
        });

        Assert.Equal("новое-после-ребута.trycloudflare.com", link.AccessReport!.AccessHost);
        Assert.Equal(AccessMode.Tunnel, link.AccessReport.AccessMode);
    }

    [Fact]
    public async Task StartAsync_SendsBootTimeToHub()
    {
        // Hub по boot-time отличает реальный ребут от лага heartbeat под нагрузкой.
        var boot = new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero);
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1", boot);

        await session.StartAsync();

        Assert.Equal(boot, link.RegisteredBootTime);
    }

    [Fact]
    public async Task ResumeAsync_SendsBootTimeToHub()
    {
        // После ребута агент поднимается через Resume — именно здесь hub и должен увидеть
        // новый boot-time, иначе перезагрузка останется незамеченной.
        var boot = new DateTimeOffset(2026, 7, 28, 13, 5, 0, TimeSpan.Zero);
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1", boot);

        await session.ResumeAsync(new RevertState { Sz = "156864" });

        Assert.Equal(boot, link.RegisteredBootTime);
    }

    [Fact]
    public async Task ResumeAsync_ResumesAccessConnectsAndRegisters()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.ResumeAsync(new RevertState { Sz = "156864" });

        Assert.Equal(1, mgr.ResumeCalls);
        Assert.Equal(0, mgr.OpenCalls);
        Assert.True(link.Connected);
        Assert.Equal("156864", link.RegisteredSz);
    }

    [Fact]
    public async Task Completion_CompletesAfterRevert()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");
        await session.ResumeAsync(new RevertState { Sz = "156864" });
        Assert.False(session.Completion.IsCompleted);

        await link.FireRevert("156864");

        Assert.True(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task HeartbeatOnceAsync_SendsHeartbeat()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");
        await session.StartAsync();

        await session.HeartbeatOnceAsync();

        Assert.Equal(1, link.Heartbeats);
    }

    [Fact]
    public async Task RevertFromHub_RevertsOnceAndDisposesLink()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await link.FireRevert("156864");
        await link.FireRevert("156864"); // повторный триггер

        Assert.Equal(1, mgr.RevertCalls);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task RevertFromHub_SendsRevertResultBeforeDisposingLink()
    {
        // Регрессия (бэклог п.119): итог отката раньше нигде не отправлялся — «close» по
        // офлайн-СЗ не мог подтвердить полноту отката иначе как походом к машине.
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await link.FireRevert("156864");

        var result = Assert.Single(link.RevertResults);
        Assert.Equal("156864", result.Sz);
        Assert.Equal(new[] { "sshd", "учётка svc-diag" }, result.Done);
        Assert.True(result.AllClean);
    }

    [Fact]
    public async Task RevertLocalAsync_RevertsOnce()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await session.RevertAsync();
        await session.RevertAsync();

        Assert.Equal(1, mgr.RevertCalls);
    }
}
