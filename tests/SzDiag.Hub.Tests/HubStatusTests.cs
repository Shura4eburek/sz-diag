using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class HubStatusTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szstatus-{Guid.NewGuid():N}");

    public HubStatusTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "agent-dist"));
        File.WriteAllText(Path.Combine(_dir, "agent-dist", "version.txt"), "76a6189\n");
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={Path.Combine(_dir, "hub.db")}")
             .UseSetting("Hub:KnowledgeBaseRoot", Path.Combine(_dir, "kb"))
             .UseSetting("Hub:AgentDistRoot", Path.Combine(_dir, "agent-dist"))
             .UseSetting("Hub:KbBackup:Enabled", "false")
             .WithoutSystemLogging());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Status_ReportsAgentPackage_KbBackupOff_TunnelOff()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");

        var s = (await c.GetFromJsonAsync<HubStatus>(HubStatusRoutes.Status))!;

        Assert.Equal("76a6189", s.AgentPackageVersion);
        Assert.False(s.KbBackup.Enabled);
        Assert.Equal(TunnelStates.Off, s.Tunnel.State);
    }

    [Fact]
    public async Task Status_NoToken_Unauthorized()
        => Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(HubStatusRoutes.Status)).StatusCode);

    [Fact]
    public void Tracker_KbBackupOutcomeAndTunnelState()
    {
        var time = new ManualTime();
        var t = new HubStatusTracker(time);
        t.KbBackupEnabled(true);
        t.KbBackupRan(new KbBackupResult(KbBackupOutcome.Pushed, 3, "выгружено"));
        t.Tunnel(TunnelStates.Running);

        var s = t.Snapshot(null);
        Assert.True(s.KbBackup.Enabled);
        Assert.Equal("Pushed", s.KbBackup.Outcome);
        Assert.Equal(time.Now, s.KbBackup.LastRunAt);
        Assert.Equal(TunnelStates.Running, s.Tunnel.State);
        Assert.Equal(time.Now, s.Tunnel.Since);

        t.KbBackupCrashed("git сломался");
        Assert.Equal("Failed", t.Snapshot(null).KbBackup.Outcome);
        Assert.Equal("git сломался", t.Snapshot(null).KbBackup.Message);
    }

    [Fact]
    public async Task KbBackupService_ReportsEachRun()
    {
        var tracker = new HubStatusTracker(TimeProvider.System);
        var opts = new HubOptions { KbBackup = new KbBackupOptions { Enabled = true, Interval = TimeSpan.FromHours(1) } };
        var service = new KbBackupService(new OkBackup(), Options.Create(opts), NullLogger<KbBackupService>.Instance, tracker);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var kb = tracker.Snapshot(null).KbBackup;
        Assert.True(kb.Enabled);
        Assert.Equal("NoChanges", kb.Outcome);
        Assert.NotNull(kb.LastRunAt);
    }

    [Fact]
    public async Task TunnelService_MissingCloudflared_ReportsNotFound()
    {
        var tracker = new HubStatusTracker(TimeProvider.System);
        var service = new HubTunnelService(
            new HubTunnelOptions { Enabled = true, ExecutablePath = Path.Combine(_dir, "нет-cloudflared.exe") },
            NullLogger<HubTunnelService>.Instance, _dir, tracker);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(TunnelStates.NotFound, tracker.Snapshot(null).Tunnel.State);
    }

    private sealed class OkBackup : IKbBackup
    {
        public Task<KbBackupResult> RunAsync(CancellationToken ct)
            => Task.FromResult(new KbBackupResult(KbBackupOutcome.NoChanges, 0, "изменений нет"));
    }
}
