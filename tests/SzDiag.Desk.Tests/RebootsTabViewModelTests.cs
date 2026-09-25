using SzDiag.Contracts;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class RebootsTabViewModelTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Events_NewestFirst_KindBugcheckUptimeActivity()
    {
        var api = new FakeHubApi
        {
            Reboots = sz => new RebootTimeline(sz, new[]
            {
                new RebootEvent(sz, T, null, null, 4 * 3600 + 12 * 60, "OCCT Power", ShutdownKind.HardOff),
                new RebootEvent(sz, T.AddHours(5), null, null, 1800, null, ShutdownKind.Bsod,
                    RebootSource.Journal, Bugcheck: 0x50),
            }, 4 * 3600 + 12 * 60),
        };
        var vm = new RebootsTabViewModel(api);
        await vm.RefreshAsync("161432", default);

        Assert.Equal(2, vm.Rows.Count);
        Assert.StartsWith("BSOD 0x50 PAGE_FAULT_IN_NONPAGED_AREA", vm.Rows[0].Kind);
        Assert.Contains("из журнала клиента", vm.Rows[0].Detail);
        Assert.Equal("обрыв питания", vm.Rows[1].Kind);
        Assert.Contains("продержалась 4ч 12м", vm.Rows[1].Detail);
        Assert.Contains("занята: OCCT Power", vm.Rows[1].Detail);
        Assert.Contains("событий: 2", vm.Summary);
    }

    [Fact]
    public async Task None_SaysSo()
    {
        var vm = new RebootsTabViewModel(new FakeHubApi { Reboots = sz => new RebootTimeline(sz, Array.Empty<RebootEvent>(), null) });
        await vm.RefreshAsync("161432", default);
        Assert.Empty(vm.Rows);
        Assert.StartsWith("вырубонов не зафиксировано", vm.Summary);
    }

    [Fact]
    public async Task HubError_SaysSo_KeepsRows()
    {
        var ok = true;
        var api = new FakeHubApi
        {
            Reboots = sz => ok
                ? new RebootTimeline(sz, new[] { new RebootEvent(sz, T, null, null, 60, null, ShutdownKind.HardOff) }, 60)
                : throw new HttpRequestException("refused"),
        };
        var vm = new RebootsTabViewModel(api);
        await vm.RefreshAsync("161432", default);
        ok = false;
        await vm.RefreshAsync("161432", default);

        Assert.Single(vm.Rows);
        Assert.Contains("hub не отдал", vm.Summary);
    }

    [Fact]
    public void RefreshOnReboot_AndEvery30s()
    {
        // Ревью I-3: ⚡N растёт только на отказах — ребут кнопкой, сон и события, влитые из журнала
        // клиента позже, иначе не видны до смены СЗ. Запрос — только SQLite hub, exec не трогает.
        IInspectorTab tab = new RebootsTabViewModel(new FakeHubApi());
        Assert.True(tab.RefreshOnReboot);
        Assert.Equal(TimeSpan.FromSeconds(30), tab.Interval);
    }
}
