using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HubAutostartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szhub-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _started = new();

    public HubAutostartTests() => Directory.CreateDirectory(Path.Combine(_dir, "desk"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Script()
    {
        var path = Path.Combine(_dir, "start-hub.cmd");
        File.WriteAllText(path, "@echo off");
        return path;
    }

    private HubAutostart New(bool up) => new(_ => Task.FromResult(up), s => { _started.Add(s); return true; });

    [Fact]
    public async Task LocalHubDown_StartsScript()
    {
        var script = Script();
        var msg = await New(up: false).EnsureAsync("http://localhost:5099", script, default);
        Assert.Equal(new[] { script }, _started);
        Assert.Contains("запущен", msg);
    }

    [Fact]
    public async Task HubUp_NothingStarted()
    {
        await New(up: true).EnsureAsync("http://localhost:5099", Script(), default);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("http://192.168.1.10:5099")]
    [InlineData("https://hub.example.com")]
    public async Task RemoteHub_NeverStarted(string url)
    {
        // Hub на другой машине отсюда не поднять — и пытаться не надо.
        var msg = await New(up: false).EnsureAsync(url, Script(), default);
        Assert.Empty(_started);
        Assert.Contains("не на этой машине", msg);
    }

    [Fact]
    public async Task NoScript_SaysBuildDist()
    {
        var msg = await New(up: false).EnsureAsync("http://127.0.0.1:5099", null, default);
        Assert.Empty(_started);
        Assert.Contains("build-dist", msg);
    }

    [Fact]
    public void FindScript_NextToDeskInDist()
    {
        // dist\host\desk\SzDiag.Desk.exe → dist\host\start-hub.cmd
        var script = Script();
        Assert.Equal(script, HubAutostart.FindScript(Path.Combine(_dir, "desk"), Path.Combine(_dir, "нет-репо")));
        Assert.Null(HubAutostart.FindScript(Path.Combine(_dir, "desk", "bin"), Path.Combine(_dir, "нет-репо")));
    }
}
