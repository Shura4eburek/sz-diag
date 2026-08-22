using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Одноразовое «переприменено — проверено» проигрывает гонку: на 260306 агент
/// проверил заморозку через секунду после применения, а через 32 секунды оркестратор поднял
/// BITS и разморозил wuauserv — лог агента вводил в заблуждение сильнее молчания (бэклог
/// п.114). Заморозку надо ДЕРЖАТЬ, пока висит маркер.</summary>
public class FreezeHoldLoopTests
{
    private const string Frozen =
        "svc:wuauserv=4\nstate:wuauserv=Stopped\nsvc:UsoSvc=4\nstate:UsoSvc=Stopped\n" +
        "svc:WaaSMedicSvc=4\nstate:WaaSMedicSvc=Stopped\n" +
        "pol:NoAutoUpdate=1\npol:WUServer=http://127.0.0.1:8530\nmarker:True\n";

    private static readonly string Drifted = Frozen
        .Replace("svc:wuauserv=4", "svc:wuauserv=3")
        .Replace("state:wuauserv=Stopped", "state:wuauserv=Running");

    private sealed class ScriptedPs : IPowerShellRunner
    {
        private readonly Queue<string> _verifyOutputs;
        public int FreezeRuns;

        public ScriptedPs(params string[] verifyOutputs) => _verifyOutputs = new(verifyOutputs);

        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
        {
            if (script.Contains("Stop-Service"))   // скрипт заморозки
            {
                Interlocked.Increment(ref FreezeRuns);
                return new PsResult(0, "frozen", "");
            }
            // скрипт verify
            lock (_verifyOutputs)
                return new PsResult(0, _verifyOutputs.Count > 1 ? _verifyOutputs.Dequeue() : _verifyOutputs.Peek(), "");
        }
    }

    [Fact]
    public async Task HoldLoop_ReappliesWhenOrchestratorUnfreezes()
    {
        // Первый verify — дрейф (оркестратор поднял wuauserv), после переприменения — норма.
        var ps = new ScriptedPs(Drifted, Frozen);
        var notes = new List<string>();
        using var cts = new CancellationTokenSource();

        var loop = WindowsUpdateFreezeGuard.StartHoldLoop(ps, cts.Token,
            (plain, _) => { lock (notes) notes.Add(plain); },
            intervalSeconds: 0, isMarked: () => true);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ps.FreezeRuns == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        cts.Cancel();
        await loop;

        Assert.True(ps.FreezeRuns >= 1, "дрейф должен вызвать переприменение");
        lock (notes)
            Assert.Contains(notes, n => n.Contains("оркестратор") || n.Contains("переприменена"));
    }

    [Fact]
    public async Task HoldLoop_QuietWhileFreezeHolds()
    {
        var ps = new ScriptedPs(Frozen);
        var notes = new List<string>();
        using var cts = new CancellationTokenSource();

        var loop = WindowsUpdateFreezeGuard.StartHoldLoop(ps, cts.Token,
            (plain, _) => { lock (notes) notes.Add(plain); },
            intervalSeconds: 0, isMarked: () => true);

        await Task.Delay(500);
        cts.Cancel();
        await loop;

        Assert.Equal(0, ps.FreezeRuns);
        lock (notes) Assert.Empty(notes);   // тишина, пока всё держится — не спамим лог
    }

    [Fact]
    public async Task HoldLoop_DoesNothingWithoutMarker()
    {
        var ps = new ScriptedPs(Drifted);
        using var cts = new CancellationTokenSource();

        var loop = WindowsUpdateFreezeGuard.StartHoldLoop(ps, cts.Token, (_, _) => { },
            intervalSeconds: 0, isMarked: () => false);

        await Task.Delay(300);
        cts.Cancel();
        await loop;

        Assert.Equal(0, ps.FreezeRuns);
    }
}
