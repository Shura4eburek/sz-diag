using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class InspectorViewModelTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeInspectorTab _jobs = new(TimeSpan.FromSeconds(3));
    private readonly FakeInspectorTab _reboots = new(null, onReboot: true);

    private InspectorViewModel New() => new(new IInspectorTab?[] { null, _jobs, _reboots }, _clock);

    [Fact]
    public void Select_RefreshesVisibleTabOnly()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        Assert.Equal(new[] { "161432" }, _jobs.Refreshed);
        Assert.Empty(_reboots.Refreshed);
    }

    [Fact]
    public async Task Tick_RespectsInterval()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        await vm.TickAsync();
        Assert.Single(_jobs.Refreshed);

        _clock.Advance(TimeSpan.FromSeconds(3));
        await vm.TickAsync();
        Assert.Equal(2, _jobs.Refreshed.Count);
    }

    [Fact]
    public async Task HiddenTab_NeverPolled()
    {
        // Опрос exec под нагрузкой дорог: открытый на «Обзоре» инспектор не дёргает ни одну вкладку.
        var vm = New();
        vm.Select("161432");
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            await vm.TickAsync();
        }
        Assert.Empty(_jobs.Refreshed);
        Assert.Empty(_reboots.Refreshed);
    }

    [Fact]
    public async Task NoIntervalTab_OnlyOnOpen()
    {
        var vm = New();
        vm.Select("161432");
        vm.SelectedIndex = 2;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await vm.TickAsync();
        Assert.Single(_reboots.Refreshed);
    }

    [Fact]
    public void OtherSz_ClearsAllTabs_RefreshesVisible()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        vm.Select("161501");
        Assert.Equal(2, _jobs.Cleared);
        Assert.Equal(2, _reboots.Cleared);
        Assert.Equal(new[] { "161432", "161501" }, _jobs.Refreshed);
    }

    [Fact]
    public void RebootCountChanged_RefreshesRebootTab()
    {
        var vm = New();
        vm.SelectedIndex = 2;
        vm.Select("161432");
        vm.OnRebootCountChanged();
        Assert.Equal(2, _reboots.Refreshed.Count);
    }

    [Fact]
    public async Task InFlight_NoDoubleRefresh()
    {
        _jobs.Gate = new TaskCompletionSource();
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        _clock.Advance(TimeSpan.FromSeconds(10));
        await Task.WhenAny(vm.TickAsync(), Task.Delay(100));
        Assert.Single(_jobs.Refreshed);
        _jobs.Gate.SetResult();
    }

    [Fact]
    public void NoSz_NothingRefreshed()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        Assert.Empty(_jobs.Refreshed);
    }
}
