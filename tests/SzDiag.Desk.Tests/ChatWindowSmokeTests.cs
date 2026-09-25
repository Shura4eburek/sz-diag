using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.Views;

namespace SzDiag.Desk.Tests;

public class ChatWindowSmokeTests
{
    private static (MainWindow W, MainViewModel Vm, ChatHarness H) Open()
    {
        var h = new ChatHarness();
        var sessions = new[] { new SessionInfo("161432", "10.0.0.5", "PC", SessionStatus.Online,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        // Окно при открытии само запускает опрос: фейк hub обязан отдавать ту же СЗ, иначе первый
        // же опрос уберёт её из списка и сбросит выбор.
        var api = new FakeHubApi { Sessions = () => sessions };
        var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), new DeskUiState(),
            TimeProvider.System, h.Services);
        var w = new MainWindow(vm);
        w.Show();
        vm.Apply(HubSnapshot.Empty with { SessionsOkAt = DateTimeOffset.UtcNow, Sessions = sessions });
        return (w, vm, h);
    }

    [AvaloniaFact]
    public void SelectSz_StartButton_ThenChatPane()
    {
        var (w, vm, h) = Open();
        using var _ = h;
        vm.Selected = vm.Items.Single();

        Assert.True(w.FindControl<Button>("StartSessionButton")!.IsEffectivelyVisible);
        Assert.False(w.FindControl<ChatView>("ChatPane")!.IsEffectivelyVisible);

        vm.StartSessionCommand.Execute(null);
        Assert.False(w.FindControl<Button>("StartSessionButton")!.IsEffectivelyVisible);
        Assert.True(w.FindControl<ChatView>("ChatPane")!.IsEffectivelyVisible);
        Assert.True(w.FindControl<Button>("TerminalButton")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task SendAndAnswer_RenderInFeed()
    {
        var (w, vm, h) = Open();
        using var _ = h;
        vm.Selected = vm.Items.Single();
        vm.StartSessionCommand.Execute(null);

        var input = w.FindControl<ChatView>("ChatPane")!.FindControl<TextBox>("ChatInput")!;
        input.Text = "привет";
        await vm.ActiveChat!.SendCommand.ExecuteAsync(null);
        h.Last.Emit(Fixture.Line("simple-turn.jsonl", e => e is AssistantText));
        h.Last.Emit(Fixture.Line("tool-turn.jsonl", e => e is ToolUse));
        Dispatcher.UIThread.RunJobs();

        Assert.True(string.IsNullOrEmpty(input.Text));   // пустой TextBox у Avalonia отдаёт null
        Assert.Collection(vm.ActiveChat.Items,
            i => Assert.IsType<UserFeedItem>(i),
            i => Assert.IsType<AssistantFeedItem>(i),
            i => Assert.IsType<ToolFeedItem>(i));
    }
}
