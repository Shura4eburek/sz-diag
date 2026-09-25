using System.Text.Json;
using SzDiag.Claude;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class ChatViewModelTests : IDisposable
{
    private const string SpikeSession = "8c8e3bf5-ea25-4879-a3dc-566095eba936";
    private readonly ChatHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static async Task Send(ChatViewModel vm, string text)
    {
        vm.Draft = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Send_ClearsDraft_ShowsBubble_Working()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "  проверь SMART  ");

        Assert.Equal("", vm.Draft);
        Assert.Equal("проверь SMART", Assert.IsType<UserFeedItem>(Assert.Single(vm.Items)).Text);
        Assert.Equal(SessionState.Working, vm.State);
        Assert.True(vm.CanStop);
    }

    [Fact]
    public async Task EmptyDraft_NotSent()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "   ");
        Assert.Empty(_h.Processes);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Stop_ReturnsQueuedToDraft()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "первое");
        await Send(vm, "второе");
        Assert.Equal(1, vm.Queued);
        Assert.True(vm.HasQueue);

        vm.Draft = "третье";
        var stop = vm.StopCommand.ExecuteAsync(null);
        _h.Last.Emit(Fixture.Line("interrupt-turn.jsonl", e => e is TurnResult { Interrupted: true }));
        await stop;

        Assert.Equal("второе\n\nтретье", vm.Draft);
        Assert.Equal(0, vm.Queued);
        Assert.Equal(SessionState.Idle, vm.State);
    }

    [Fact]
    public async Task PermissionCard_AllowResolvesBroker()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "создай файл");
        var ask = _h.Broker.AskAsync("161432", "Write", J("""{"file_path":"C:\\a.txt"}"""), "t1", default);

        var card = Assert.Single(vm.Items.OfType<PermissionFeedItem>());
        Assert.True(card.IsPending);
        Assert.Equal(SessionState.WaitingPermission, vm.State);
        card.AllowCommand.Execute(null);

        Assert.True((await ask).Allow);
        Assert.Equal("разрешено", card.ResultText);
        Assert.Equal(SessionState.Working, vm.State);
    }

    [Fact]
    public void Replay_StalePermission_Expired()
    {
        // Запрос из прошлого запуска Desk: брокер о нём не знает — карточка не должна ждать вечно.
        _h.Transcripts.Append("161432", DeskLines.Serialize(new PermissionAsked("old", "Bash", J("{}"), null))!);
        var vm = _h.Chat("161432");
        var card = Assert.Single(vm.Items.OfType<PermissionFeedItem>());
        Assert.False(card.IsPending);
        Assert.Contains("истёк", card.ResultText);
    }

    [Fact]
    public async Task OpenInTerminal_StopsSessionAndLaunches()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "привет");
        _h.Last.Emit(Fixture.Line("simple-turn.jsonl", e => e is SystemInit));

        await vm.OpenInTerminalCommand.ExecuteAsync(null);

        Assert.Equal(("161432", SpikeSession), Assert.Single(_h.Terminal.Opened));
        Assert.True(_h.Last.Stopped);
        Assert.Equal(SessionState.Stopped, vm.State);
        Assert.Contains(vm.Items, i => i is NoteFeedItem n && n.Text.Contains("два писателя"));
    }

    [Fact]
    public async Task OpenInTerminal_NoSessionYet_Note()
    {
        var vm = _h.Chat("161432");
        await vm.OpenInTerminalCommand.ExecuteAsync(null);
        Assert.Empty(_h.Terminal.Opened);
        Assert.Contains(vm.Items, i => i is NoteFeedItem n && n.Text.Contains("ещё не началась"));
    }

    [Fact]
    public async Task Crash_ShowsCard_StateCrashed()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "x");
        _h.Last.Stderr.Add("boom");
        _h.Last.Exit(3);

        var card = Assert.Single(vm.Items.OfType<CrashFeedItem>());
        Assert.Contains("код 3", card.Title);
        Assert.Contains("boom", card.Details);
        Assert.Equal(SessionState.Crashed, vm.State);
        Assert.Contains("упала", vm.StateText);
    }
}
