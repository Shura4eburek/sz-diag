using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class ActionsViewModelTests
{
    private readonly FakeSzcliRunner _szcli = new();

    private async Task<ActionsViewModel> New()
    {
        var vm = new ActionsViewModel(_szcli);
        await vm.RefreshAsync("161432", default);
        return vm;
    }

    [Fact]
    public async Task DiagRun_WithSections()
    {
        var vm = await New();
        vm.DiagSections = "storage, reboots";
        await vm.DiagRunCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "diag", "run", "161432", "storage", "reboots" }, _szcli.Calls.Single());
        Assert.Contains("[код выхода 0]", vm.Log);
    }

    [Fact]
    public async Task TestRun_NeedsConfigLabel()
    {
        // Метка обязательна, как в CLI: без неё «профиль против стока» через неделю нечитаем.
        var vm = await New();
        Assert.False(vm.TestRunCommand.CanExecute(null));
        vm.TestConfig = "EXPO 6000";
        vm.TestFilter = "occt";
        Assert.True(vm.TestRunCommand.CanExecute(null));
        await vm.TestRunCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "test", "run", "161432", "occt", "--config", "EXPO 6000" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task Close_RunsOnlyAfterConfirm()
    {
        var vm = await New();
        vm.CloseCommand.Execute(null);
        Assert.Empty(_szcli.Calls);
        Assert.Contains("Закрыть СЗ 161432", vm.PendingConfirm);

        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "close", "161432" }, _szcli.Calls.Single());
        Assert.Null(vm.PendingConfirm);
    }

    [Fact]
    public async Task Unfreeze_RunsOnlyAfterConfirm_CancelDropsIt()
    {
        var vm = await New();
        vm.UnfreezeCommand.Execute(null);
        vm.CancelConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(_szcli.Calls);

        vm.UnfreezeCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "unfreeze", "161432" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task CloseForce_NeedsReason()
    {
        var vm = await New();
        Assert.False(vm.CloseForceCommand.CanExecute(null));
        vm.CloseReason = "клиент забрал машину";
        vm.CloseForceCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "close", "161432", "--force", "клиент забрал машину" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task Note_ClearedOnSuccess_KeptOnFailure()
    {
        var vm = await New();
        vm.NoteText = "свап БП на 750W";
        await vm.NoteCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "note", "161432", "свап БП на 750W" }, _szcli.Calls.Single());
        Assert.Equal("", vm.NoteText);

        _szcli.Respond = _ => new SzcliResult(1, "hub не принял");
        vm.NoteText = "ещё";
        await vm.NoteCommand.ExecuteAsync(null);
        Assert.Equal("ещё", vm.NoteText);
        Assert.Equal(1, vm.LastExitCode);
    }

    [Fact]
    public async Task WhileRunning_OtherActionsIgnored()
    {
        _szcli.Gate = new TaskCompletionSource();
        var vm = await New();
        var first = vm.SzFetchCommand.ExecuteAsync(null);
        Assert.True(vm.Running);
        Assert.False(vm.IsIdle);
        await vm.FreezeCommand.ExecuteAsync(null);
        Assert.Single(_szcli.Calls);
        _szcli.Gate.SetResult();
        await first;
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task NoSzYet_NothingRuns()
    {
        var vm = new ActionsViewModel(_szcli);
        await vm.FreezeCommand.ExecuteAsync(null);
        Assert.Empty(_szcli.Calls);
    }
}
