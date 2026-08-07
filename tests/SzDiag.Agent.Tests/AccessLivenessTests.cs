using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Watchdog срезал доступ ровно через час при живой сессии — шли `exec`/`pull`,
/// heartbeat не прерывался, а в CLI так и висело «online · готов» (бэклог п.85/п.81).</summary>
public class AccessLivenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szalive-{Guid.NewGuid():N}");
    private string StatePath => Path.Combine(_dir, "state.json");

    private static readonly DateTimeOffset Now = new(2026, 8, 7, 13, 57, 0, TimeSpan.Zero);

    public AccessLivenessTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void FreshHeartbeat_KeepsAccess()
    {
        var opened = Now.AddHours(-1);

        Assert.False(AccessLiveness.ShouldRevert(Now.AddMinutes(-1), opened, Now));
        Assert.Contains("агент жив", AccessLiveness.Explain(Now.AddMinutes(-1), opened, Now));
    }

    [Fact]
    public void SilentAgent_LosesAccess()
    {
        Assert.True(AccessLiveness.ShouldRevert(Now.AddMinutes(-30), Now.AddHours(-1), Now));
        Assert.Contains("молчит", AccessLiveness.Explain(Now.AddMinutes(-30), Now.AddHours(-1), Now));
    }

    [Fact]
    public void NoMarkAtAll_BehavesAsBefore_AndReverts()
    {
        // Агент старой сборки метку не пишет — он не должен удерживать доступ вечно.
        Assert.True(AccessLiveness.ShouldRevert(null, Now.AddHours(-1), Now));
    }

    [Fact]
    public void CeilingWins_EvenWithLiveAgent()
    {
        // Сутки удержания — это забытая сессия, а не длинный прогон.
        var opened = Now.AddHours(-30);

        Assert.True(AccessLiveness.ShouldRevert(Now.AddSeconds(-5), opened, Now));
        Assert.Contains("потолок", AccessLiveness.Explain(Now.AddSeconds(-5), opened, Now));
    }

    [Fact]
    public void TouchAndRead_RoundTrip()
    {
        AccessLiveness.Touch(StatePath);

        var seen = AccessLiveness.LastSeen(StatePath);

        Assert.NotNull(seen);
        Assert.True((DateTimeOffset.Now - seen!.Value).TotalSeconds < 30);
        Assert.True(File.Exists(AccessLiveness.PathFor(StatePath)));

        AccessLiveness.Delete(StatePath);
        Assert.Null(AccessLiveness.LastSeen(StatePath));
    }

    [Fact]
    public void BrokenMarkFile_IsTreatedAsMissing()
    {
        File.WriteAllText(AccessLiveness.PathFor(StatePath), "мусор");

        Assert.Null(AccessLiveness.LastSeen(StatePath));
        Assert.True(AccessLiveness.ShouldRevert(null, Now.AddHours(-1), Now));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
