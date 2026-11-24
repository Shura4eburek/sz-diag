using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>T040/T047: сторож сервисной ZT-сети. Клиент без активной СЗ удаляется по сроку,
/// узлы hub-машин и активных СЗ не трогаются.</summary>
public class ZtMemberSweeperTests
{
    internal sealed class FakeController : IZtControllerClient
    {
        public readonly Dictionary<string, ZtMember> Members = new();
        public readonly List<string> Deleted = [];

        public Task<IReadOnlyList<string>> ListMemberIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Members.Keys.ToList());

        public Task<ZtMember?> GetMemberAsync(string id, CancellationToken ct) =>
            Task.FromResult(Members.TryGetValue(id, out var m) ? m : null);

        public Task<bool> DeleteMemberAsync(string id, CancellationToken ct)
        {
            Deleted.Add(id);
            Members.Remove(id);
            return Task.FromResult(true);
        }
    }

    internal static IOptions<HubOptions> Opts() => Options.Create(new HubOptions
    {
        ZeroTier = new HubZeroTierOptions
        {
            Enabled = true, Network = "aa4bb3b8ba8f7d86",
            ControllerUrl = "https://ctl.test", ControllerToken = "t",
        },
    });

    private readonly ManualTime _time = new() { Now = DateTimeOffset.Parse("2026-11-25T12:00:00Z") };
    private readonly FakeController _ctl = new();
    private readonly SessionRegistry _reg = new();

    private ZtMemberSweeper Sweeper() => new(Opts(), _reg, _ctl, NullLogger<ZtMemberSweeper>.Instance, _time);

    private void Member(string id, TimeSpan age, string name = "", params string[] ips) =>
        _ctl.Members[id] = new ZtMember(id, name, _time.GetUtcNow() - age, ips);

    [Fact]
    public async Task Old_member_without_session_removed_young_one_kept()
    {
        Member("aaaaaaaaaa", TimeSpan.FromMinutes(11), ips: "10.88.0.50");
        Member("bbbbbbbbbb", TimeSpan.FromMinutes(3), ips: "10.88.0.51");

        var removed = await Sweeper().SweepOnceAsync(default);

        Assert.Equal(["aaaaaaaaaa"], removed);
        Assert.Contains("bbbbbbbbbb", _ctl.Members.Keys);
    }

    [Fact]
    public async Task Hub_machines_and_active_sessions_never_removed()
    {
        Member("93fe4a18d2", TimeSpan.FromDays(30), name: "hub-MAMORU", "10.88.0.1");
        Member("1111111111", TimeSpan.FromDays(30), ips: "10.88.0.2");
        Member("ae264cb785", TimeSpan.FromHours(5), ips: "10.88.0.95");
        _reg.Register("162003", "10.88.0.95", "PC", "conn");
        _reg.SetAccess("162003", "10.88.0.95", AccessMode.Zt, null, "ae264cb785");

        Assert.Empty(await Sweeper().SweepOnceAsync(default));
        Assert.Empty(_ctl.Deleted);
    }

    [Fact]
    public async Task Without_creation_time_age_counts_from_first_sighting()
    {
        _ctl.Members["cccccccccc"] = new ZtMember("cccccccccc", "", null, ["10.88.0.60"]);
        var s = Sweeper();

        Assert.Empty(await s.SweepOnceAsync(default));
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(["cccccccccc"], await s.SweepOnceAsync(default));
    }

    [Fact]
    public void Controller_member_json_parsed()
    {
        using var doc = JsonDocument.Parse("""
            {"id":"ae264cb785","name":"","creationTime":1795548524450,"ipAssignments":["10.88.0.95"],"authorized":true}
            """);
        var m = ZtControllerClient.Parse(doc.RootElement, "x");
        Assert.Equal("ae264cb785", m.Id);
        Assert.Equal(["10.88.0.95"], m.IpAssignments);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795548524450), m.CreatedAt);
    }

    [Fact]
    public void ReportAccess_zt_node_kept_when_later_report_omits_it()
    {
        _reg.Register("162003", "10.88.0.95", "PC", "conn");
        _reg.SetAccess("162003", "10.88.0.95", AccessMode.Zt, null, "ae264cb785");
        _reg.SetAccess("162003", "aaa.trycloudflare.com", AccessMode.Tunnel, null);

        Assert.Equal("ae264cb785", _reg.TryGetInfo("162003")!.ZtNodeId);
    }
}
