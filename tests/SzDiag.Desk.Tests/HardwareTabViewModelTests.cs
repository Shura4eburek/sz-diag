using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class HardwareTabViewModelTests
{
    [Fact]
    public async Task Passport_FromSzcli_CachedPerSz()
    {
        var szcli = new FakeSzcliRunner { Respond = a => new SzcliResult(0, $"GPU паспорт {a[2]}") };
        var vm = new HardwareTabViewModel(szcli);
        await vm.RefreshAsync("161432", default);
        Assert.Equal("GPU паспорт 161432", vm.Text);
        Assert.Equal(new[] { "hw", "passport", "161432" }, szcli.Calls.Single());

        vm.Clear();
        await vm.RefreshAsync("161432", default);
        Assert.Single(szcli.Calls);                 // тот же паспорт — из кэша

        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(2, szcli.Calls.Count);         // «обновить» — снимает заново
    }

    [Fact]
    public async Task Failure_ShowsCodeAndOutput_NotCached()
    {
        var szcli = new FakeSzcliRunner { Respond = _ => new SzcliResult(1, "СЗ 161432 не найдена") };
        var vm = new HardwareTabViewModel(szcli);
        await vm.RefreshAsync("161432", default);
        Assert.StartsWith("паспорт не снялся (код 1)", vm.Text);
        await vm.RefreshAsync("161432", default);
        Assert.Equal(2, szcli.Calls.Count);
    }
}
