using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Под FurMark sshd не подвис, а **умер** — и сам не вернулся: канал был потерян на
/// час, пока hub показывал СЗ online (бэклог п.95).</summary>
public class SshdWatchdogTests
{
    private sealed class FakeSshd : ISshServer
    {
        public int Starts;
        public string? LastKeyLine;
        public int LastPort;
        public string? LastTask;
        public string WorkDir => Path.GetTempPath();
        public void Start(int port, string authorizedKeyLine, string taskName)
        {
            Interlocked.Increment(ref Starts);
            LastKeyLine = authorizedKeyLine;
            LastPort = port;
            LastTask = taskName;
        }
        public void Stop(string taskName) { }
    }

    [Fact]
    public async Task DeadSshd_IsRestartedWithSameKeyAndTask()
    {
        var sshd = new FakeSshd();
        using var cts = new CancellationTokenSource();

        var loop = SshdWatchdog.Start(sshd, 22, "ssh-ed25519 AAAA szdiag-161312", "szdiag-sshd-161312",
            cts.Token, (_, _) => { }, intervalSeconds: 1, isAlive: () => false);

        while (sshd.Starts == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.Equal(22, sshd.LastPort);
        Assert.Equal("szdiag-sshd-161312", sshd.LastTask);
        Assert.Contains("szdiag-161312", sshd.LastKeyLine);
    }

    [Fact]
    public async Task LiveSshd_IsLeftAlone()
    {
        // Лишний перезапуск рвёт активные сессии — под нагрузкой sshd может просто не
        // отвечать, оставаясь живым, и это не повод его трогать.
        var sshd = new FakeSshd();
        using var cts = new CancellationTokenSource();

        var loop = SshdWatchdog.Start(sshd, 22, "key", "task", cts.Token, (_, _) => { },
            intervalSeconds: 1, isAlive: () => true);
        await Task.Delay(150);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.Equal(0, sshd.Starts);
    }

    [Fact]
    public async Task RestartFailure_DoesNotKillTheLoop()
    {
        var attempts = 0;
        var sshd = new ThrowingSshd(() => attempts++);
        using var cts = new CancellationTokenSource();

        var loop = SshdWatchdog.Start(sshd, 22, "key", "task", cts.Token, (_, _) => { },
            intervalSeconds: 1, isAlive: () => false);
        while (attempts < 2) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.True(attempts >= 2);   // следующая итерация пробует снова
    }

    private sealed class ThrowingSshd : ISshServer
    {
        private readonly Action _onStart;
        public ThrowingSshd(Action onStart) => _onStart = onStart;
        public string WorkDir => Path.GetTempPath();
        public void Start(int port, string authorizedKeyLine, string taskName)
        {
            _onStart();
            throw new SshdStartException("порт занят");
        }
        public void Stop(string taskName) { }
    }
}
