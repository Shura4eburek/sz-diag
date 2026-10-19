using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class PermissionBrokerTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
    private readonly PermissionBroker _b = new(TimeProvider.System);

    [Fact]
    public async Task Ask_WaitsUntilResolved_AllowEchoesInput()
    {
        PendingPermission? seen = null;
        _b.Requested += p => seen = p;
        var ask = _b.AskAsync("161432", "Bash", J("""{"command":"dir"}"""), "t1", default);

        Assert.False(ask.IsCompleted);
        Assert.Equal(1, _b.PendingCount);
        Assert.Equal("161432", seen!.Key);
        Assert.True(_b.Resolve(seen.RequestId, true));

        var v = await ask;
        Assert.True(v.Allow);
        Assert.Equal("dir", v.UpdatedInput!.Value.GetProperty("command").GetString());
        Assert.Equal(0, _b.PendingCount);
        Assert.False(_b.Resolve(seen.RequestId, true));   // повторный ответ — no-op
    }

    [Fact]
    public async Task Cancel_Denies()
    {
        using var cts = new CancellationTokenSource();
        var ask = _b.AskAsync("161432", "Bash", J("{}"), null, cts.Token);
        cts.Cancel();
        var v = await ask;
        Assert.False(v.Allow);
        Assert.Equal("запрос отменён", v.Message);
    }

    [Fact]
    public async Task DenyAll_OnlyThatKey()
    {
        var a = _b.AskAsync("161432", "Bash", J("{}"), null, default);
        var b = _b.AskAsync("161501", "Bash", J("{}"), null, default);
        _b.DenyAll("161432", "сессия остановлена");

        Assert.Equal("сессия остановлена", (await a).Message);
        Assert.False(b.IsCompleted);
        Assert.Single(_b.Pending("161501"));
        Assert.Empty(_b.Pending("161432"));
    }

    [Fact]
    public void Verdict_Json()
    {
        var allow = JsonDocument.Parse(new PermissionVerdict(true, J("""{"a":1}"""), null).ToJson()).RootElement;
        Assert.Equal("allow", allow.GetProperty("behavior").GetString());
        Assert.Equal(1, allow.GetProperty("updatedInput").GetProperty("a").GetInt32());

        var deny = JsonDocument.Parse(new PermissionVerdict(false, null, "нет").ToJson()).RootElement;
        Assert.Equal("deny", deny.GetProperty("behavior").GetString());
        Assert.Equal("нет", deny.GetProperty("message").GetString());
    }
}
