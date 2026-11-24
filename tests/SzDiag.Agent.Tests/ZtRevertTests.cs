using SzDiag.Agent;
using SzDiag.Agent.Zt;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>T038: шаг 11 (SSH через сервисную ZT-сеть) — как туннель: открыт в Open, снят в
/// Revert под своим флагом, ПЕРВЫМ (раньше sshd), идемпотентно; storage стирается.</summary>
public class ZtRevertTests : IDisposable
{
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"szstate-zt-{Guid.NewGuid():N}.json");
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"szzt-{Guid.NewGuid():N}", "zt", "162003");
    private int _order;
    private int Tick() => ++_order;

    public void Dispose()
    {
        try { File.Delete(_statePath); } catch { }
        var top = Path.GetDirectoryName(Path.GetDirectoryName(_storage))!;
        if (Directory.Exists(top)) Directory.Delete(top, true);
    }

    private sealed class Ps : IPowerShellRunner
    {
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null) => new(0, "", "");
    }

    private sealed class Ssh(Func<int>? tick = null, bool throwOnStop = false) : ISshServer
    {
        public int StoppedAt;
        public string WorkDir => Path.Combine(Path.GetTempPath(), $"szssh-{Guid.NewGuid():N}");
        public void Start(int port, string authorizedKeyLine, string taskName) { }
        public void Stop(string taskName)
        {
            StoppedAt = tick?.Invoke() ?? 0;
            if (throwOnStop) throw new UnauthorizedAccessException("sshd не снялся");
        }
    }

    private sealed class FakeZt(string storage, Func<int>? tick = null, bool noNode = false, bool throwOnStop = false) : IZtAccess
    {
        public int Starts, Stops, StoppedAt;
        public string? StoppedStorage;

        public ZtAccessInfo? Start(int sshPort)
        {
            Starts++;
            return noNode ? null : new ZtAccessInfo("ae264cb785", "10.88.0.95", "aa4bb3b8ba8f7d86", storage);
        }

        public void Stop(string storagePath)
        {
            Stops++;
            StoppedAt = tick?.Invoke() ?? 0;
            StoppedStorage = storagePath;
            if (throwOnStop) throw new IOException("zt не снялся");
        }
    }

    private static AccessSpec Spec() => new("162003", "svc-diag", "ssh-ed25519 AAAA", 22, TimeSpan.FromHours(6));

    private RevertState ZtState() => new()
    {
        Sz = "162003",
        SshdTaskName = "szdiag-sshd-162003",
        CreatedSshdTask = true,
        JoinedZt = true,
        ZtStoragePath = _storage,
        ZtNodeId = "ae264cb785",
        ZtAddress = "10.88.0.95",
        ZtNetwork = "aa4bb3b8ba8f7d86",
    };

    [Fact]
    public void Open_writes_zt_step_into_state_and_persists_it()
    {
        var zt = new FakeZt(_storage);
        var state = new WindowsSystemAccessManager(new Ps(), new Ssh(), _statePath, zt: zt).Open(Spec());

        Assert.True(state.JoinedZt);
        Assert.Equal((_storage, "ae264cb785", "10.88.0.95", "aa4bb3b8ba8f7d86"),
            (state.ZtStoragePath, state.ZtNodeId, state.ZtAddress, state.ZtNetwork));
        var persisted = RevertStateStore.Load(_statePath)!;
        Assert.True(persisted.JoinedZt);
        Assert.Equal(_storage, persisted.ZtStoragePath);
    }

    [Fact]
    public void Open_without_node_leaves_step_unapplied()
    {
        var state = new WindowsSystemAccessManager(new Ps(), new Ssh(), _statePath, zt: new FakeZt(_storage, noNode: true))
            .Open(Spec());
        Assert.False(state.JoinedZt);
        Assert.True(state.CreatedSshdTask);
    }

    [Fact]
    public void Revert_stops_zt_before_sshd_and_is_idempotent()
    {
        var zt = new FakeZt(_storage, Tick);
        var ssh = new Ssh(Tick);
        var manager = new WindowsSystemAccessManager(new Ps(), ssh, _statePath, zt: zt);
        var state = ZtState();

        var outcome = manager.Revert(state);

        Assert.Contains("zt-доступ", outcome.Done);
        Assert.True(zt.StoppedAt < ssh.StoppedAt, $"zt снят {zt.StoppedAt}-м, sshd — {ssh.StoppedAt}-м");
        Assert.Equal(_storage, zt.StoppedStorage);
        Assert.False(state.JoinedZt);

        manager.Revert(state);
        Assert.Equal(1, zt.Stops);
    }

    [Fact]
    public void Headless_revert_without_zt_access_wipes_storage_from_state()
    {
        Directory.CreateDirectory(Path.Combine(_storage, "networks.d"));
        File.WriteAllText(Path.Combine(_storage, "identity.secret"), "x");

        var outcome = new WindowsSystemAccessManager(new Ps(), new Ssh(), _statePath).Revert(ZtState());

        Assert.Contains("zt-доступ", outcome.Done);
        Assert.False(Directory.Exists(_storage));
    }

    [Fact]
    public void Zt_step_failure_does_not_stop_rest_of_revert()
    {
        var ssh = new Ssh(Tick);
        var outcome = new WindowsSystemAccessManager(new Ps(), ssh, _statePath,
            zt: new FakeZt(_storage, Tick, throwOnStop: true)).Revert(ZtState());

        Assert.Contains(outcome.Failed, f => f.Step == "zt-доступ");
        Assert.True(ssh.StoppedAt > 0);
    }

    [Fact]
    public void Zt_is_still_stopped_when_sshd_step_fails()
    {
        var zt = new FakeZt(_storage, Tick);
        var outcome = new WindowsSystemAccessManager(new Ps(), new Ssh(Tick, throwOnStop: true), _statePath, zt: zt)
            .Revert(ZtState());

        Assert.Equal(1, zt.Stops);
        Assert.Contains(outcome.Failed, f => f.Step == "sshd");
    }

    [Fact]
    public void Resume_restarts_forwarder_when_session_used_zt()
    {
        var zt = new FakeZt(_storage);
        var state = ZtState();
        new WindowsSystemAccessManager(new Ps(), new Ssh(), _statePath, zt: zt).Resume(state, Spec());
        Assert.Equal(1, zt.Starts);
        Assert.True(state.JoinedZt);
    }

    // Ревью: после ребута hub нашёлся мимо ZT — прежний адрес мёртв и не должен перебивать туннель.
    [Fact]
    public void Resume_without_node_drops_dead_zt_address_but_keeps_storage_for_revert()
    {
        var state = ZtState();
        state.StartedQuickTunnel = true;
        state.QuickTunnelHost = "new-name.trycloudflare.com";

        new WindowsSystemAccessManager(new Ps(), new Ssh(), _statePath, zt: new FakeZt(_storage, noNode: true))
            .Resume(state, Spec());

        Assert.False(state.JoinedZt);
        Assert.Equal("", state.ZtAddress);
        Assert.Equal(_storage, state.ZtStoragePath);
        Assert.Equal(AccessMode.Tunnel, AccessReporter.BuildReport(state, "162003", false).AccessMode);
    }
}
