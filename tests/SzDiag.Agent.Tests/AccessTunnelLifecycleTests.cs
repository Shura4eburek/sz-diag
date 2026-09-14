using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Туннель — такой же шаг доступа, как sshd и учётка: открыт в Open, снят в Revert
/// под своим флагом. Забытая ветка отката оставляет на клиентской машине живой туннель,
/// опубликованный в интернет.</summary>
public class AccessTunnelLifecycleTests : IDisposable
{
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"szstate-cfd-{Guid.NewGuid():N}.json");

    /// <summary>Общий счётчик порядка: важно не «оба снялись», а «туннель снялся РАНЬШЕ».</summary>
    private int _порядок;

    private sealed class ФейкPs : IPowerShellRunner
    {
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
            => new(0, "", "");
    }

    private sealed class ФейкТуннель : IAccessTunnel
    {
        public string? ИмяКВыдаче { get; init; } = "aaa-bbb.trycloudflare.com";
        public bool Снят { get; private set; }
        public int ПорядокСнятия { get; private set; }
        public Func<int>? Тикер { get; init; }

        public string? Start(int sshPort, string taskName, TimeSpan timeout) => ИмяКВыдаче;

        public void Stop(string taskName)
        {
            Снят = true;
            ПорядокСнятия = Тикер?.Invoke() ?? 0;
        }
    }

    private sealed class ФейкSsh : ISshServer
    {
        public bool ThrowOnStop { get; init; }
        public int ПорядокСнятия { get; private set; }
        public Func<int>? Тикер { get; init; }
        public string WorkDir => Path.Combine(Path.GetTempPath(), $"szssh-{Guid.NewGuid():N}");
        public void Start(int port, string authorizedKeyLine, string taskName) { }
        public void Stop(string taskName)
        {
            ПорядокСнятия = Тикер?.Invoke() ?? 0;
            if (ThrowOnStop) throw new UnauthorizedAccessException("sshd не снялся");
        }
    }

    private static AccessSpec Spec(string sz = "162003")
        => new(sz, "svc-diag", "ssh-ed25519 AAAA", 22, TimeSpan.FromHours(6));

    private static RevertState StateСТуннелем() => new()
    {
        Sz = "162003",
        SshdTaskName = "szdiag-sshd-162003",
        TunnelTaskName = "szdiag-cfd-162003",
        QuickTunnelHost = "aaa-bbb.trycloudflare.com",
        CreatedSshdTask = true,
        StartedQuickTunnel = true,
    };

    [Fact]
    public void Open_записывает_имя_туннеля_в_состояние()
    {
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath,
            new ФейкТуннель());

        var state = manager.Open(Spec());

        Assert.True(state.StartedQuickTunnel);
        Assert.Equal("aaa-bbb.trycloudflare.com", state.QuickTunnelHost);
        Assert.NotEmpty(state.TunnelTaskName);
    }

    [Fact]
    public void Туннель_не_поднялся_доступ_всё_равно_открыт()
    {
        // Без SSH заявка продолжает работать через exec-канал: валить Open нельзя.
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath,
            new ФейкТуннель { ИмяКВыдаче = null });

        var state = manager.Open(Spec());

        Assert.False(state.StartedQuickTunnel);
        Assert.True(string.IsNullOrEmpty(state.QuickTunnelHost));
        Assert.True(state.CreatedSshdTask);
    }

    [Fact]
    public void Без_туннеля_вообще_Open_работает_как_раньше()
    {
        // Прямой режим (машина в локалке бокса) — туннель не нужен и не создаётся.
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath);

        var state = manager.Open(Spec());

        Assert.False(state.StartedQuickTunnel);
        Assert.True(state.CreatedSshdTask);
    }

    [Fact]
    public void Revert_снимает_туннель_раньше_sshd()
    {
        // Иначе между снятием sshd и снятием туннеля наружу торчит опубликованная дверь
        // в уже разваливающийся доступ.
        var tunnel = new ФейкТуннель { Тикер = Тик };
        var ssh = new ФейкSsh { Тикер = Тик };
        var manager = new WindowsSystemAccessManager(new ФейкPs(), ssh, _statePath, tunnel);

        manager.Revert(StateСТуннелем());

        Assert.True(tunnel.Снят);
        Assert.True(tunnel.ПорядокСнятия < ssh.ПорядокСнятия,
            $"туннель снят {tunnel.ПорядокСнятия}-м, sshd — {ssh.ПорядокСнятия}-м");
    }

    [Fact]
    public void Туннель_снимается_даже_когда_падает_шаг_sshd()
    {
        // Инвариант «каждый шаг в своём try/catch»: на 160705 одно исключение оставило
        // доступ на машине навсегда (бэклог п.59).
        var tunnel = new ФейкТуннель { Тикер = Тик };
        var manager = new WindowsSystemAccessManager(new ФейкPs(),
            new ФейкSsh { ThrowOnStop = true, Тикер = Тик }, _statePath, tunnel);

        var outcome = manager.Revert(StateСТуннелем());

        Assert.True(tunnel.Снят);
        Assert.Contains(outcome.Failed, f => f.Step == "sshd");
        Assert.Contains("quick tunnel", outcome.Done);
    }

    [Fact]
    public void Падение_снятия_туннеля_не_срывает_остальной_откат()
    {
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath,
            new ПадающийТуннель());

        var outcome = manager.Revert(StateСТуннелем());

        Assert.Contains(outcome.Failed, f => f.Step == "quick tunnel");
        Assert.Contains("sshd", outcome.Done);
    }

    [Fact]
    public void Revert_идемпотентен_по_туннелю()
    {
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath,
            new ФейкТуннель());
        var state = StateСТуннелем();

        manager.Revert(state);
        var ex = Record.Exception(() => manager.Revert(state));

        Assert.Null(ex);
    }

    [Fact]
    public void Туннель_не_поднимался_шаг_отката_пропускается()
    {
        var tunnel = new ФейкТуннель();
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath, tunnel);

        manager.Revert(new RevertState { Sz = "162003", CreatedSshdTask = true, SshdTaskName = "x" });

        Assert.False(tunnel.Снят);
    }

    [Fact]
    public void Resume_переподнимает_туннель_с_новым_именем()
    {
        // Quick tunnel не сохраняет hostname между запусками: после ребута имя ДРУГОЕ.
        // Не переподнять — значит оставить hub с адресом мёртвого туннеля.
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath,
            new ФейкТуннель { ИмяКВыдаче = "новое-после-ребута.trycloudflare.com" });
        var state = StateСТуннелем();

        manager.Resume(state, Spec());

        Assert.True(state.StartedQuickTunnel);
        Assert.Equal("новое-после-ребута.trycloudflare.com", state.QuickTunnelHost);
    }

    [Fact]
    public void Resume_без_туннеля_в_прошлой_сессии_его_и_не_поднимает()
    {
        // Прямой режим переживает ребут прямым режимом.
        var tunnel = new ФейкТуннель();
        var manager = new WindowsSystemAccessManager(new ФейкPs(), new ФейкSsh(), _statePath, tunnel);
        var state = new RevertState { Sz = "162003", SshdTaskName = "x", CreatedSshdTask = true };

        manager.Resume(state, Spec());

        Assert.False(state.StartedQuickTunnel);
    }

    private sealed class ПадающийТуннель : IAccessTunnel
    {
        public string? Start(int sshPort, string taskName, TimeSpan timeout) => null;
        public void Stop(string taskName) => throw new IOException("задача не снялась");
    }

    private int Тик() => ++_порядок;

    public void Dispose()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { /* временный файл */ }
    }
}
