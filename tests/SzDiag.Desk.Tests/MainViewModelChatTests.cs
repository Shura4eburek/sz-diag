using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class MainViewModelChatTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ChatHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SessionInfo S(string sz, SessionStatus st = SessionStatus.Online, int reboots = 0)
        => new(sz, "10.0.0.5", "PC-" + sz, st, Now.AddHours(-1), Now, RebootCount: reboots);

    private static HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = Now };

    private MainViewModel New()
        => new(new HubPoller(new FakeHubApi(), new Clock()), new DeskUiState(), new Clock(), _h.Services);

    [Fact]
    public void SelectSzWithoutSession_CanStart_StartOpensChat()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();

        Assert.Null(vm.ActiveChat);
        Assert.True(vm.CanStartSession);
        Assert.False(vm.ShowPlaceholder);

        vm.StartSessionCommand.Execute(null);
        Assert.Equal("161432", vm.ActiveChat!.Key);
        Assert.False(vm.CanStartSession);
        Assert.Equal("161432", vm.Title);
    }

    [Fact]
    public void SelectSzWithSession_ChatOpensItself()
    {
        _h.Services.Sessions.Create("161432");
        var vm = New();
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        Assert.Equal("161432", vm.ActiveChat!.Key);
    }

    [Fact]
    public void ClosedSz_SessionArchived_AndListed()
    {
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161501");

        vm.Apply(Snap(S("161432")));

        Assert.Equal(SessionState.Archived, _h.Services.Sessions.Peek("161501")!.State);
        Assert.Equal("161501", Assert.Single(vm.Archived).Key);
        Assert.True(vm.HasArchived);
    }

    [Fact]
    public async Task ArchivedSession_Continued_NotReArchivedByNextPoll()
    {
        // Живая проверка: сообщение из «АРХИВ» снимало архив, а следующий же опрос (СЗ в списке
        // по-прежнему нет) архивировал сессию снова и останавливал только что запущенный процесс.
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161501");
        vm.Apply(Snap(S("161432")));
        var session = _h.Services.Sessions.Peek("161501")!;
        Assert.Equal(SessionState.Archived, session.State);

        await session.SendAsync("продолжим");
        vm.Apply(Snap(S("161432")));

        Assert.Equal(SessionState.Working, session.State);
        Assert.False(_h.Last.Stopped);
    }

    [Fact]
    public void Apply_BeforeFirstSessionsPoll_ArchivesNothing()
    {
        // Первым может прийти опрос передач: пустой список СЗ тогда значит «ещё не знаю».
        _h.Services.Sessions.Create("161432");
        var vm = New();
        vm.Apply(HubSnapshot.Empty);

        Assert.NotEqual(SessionState.Archived, _h.Services.Sessions.Peek("161432")!.State);
        Assert.Empty(vm.Archived);
    }

    [Fact]
    public void SelectArchived_OpensItsChat_ClearsLiveSelection()
    {
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161501");
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();

        vm.SelectedArchived = vm.Archived.Single();

        Assert.Null(vm.Selected);
        Assert.Equal("161501", vm.ActiveChat!.Key);
    }

    [Fact]
    public void MachineChanges_NotedInSession()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        var session = _h.Services.Sessions.Create("161432");

        vm.Apply(Snap(S("161432", SessionStatus.Offline)));
        Assert.Contains(session.History, e => e is DeskNote n && n.Text.StartsWith("связь с машиной пропала"));

        vm.Apply(Snap(S("161432", reboots: 1)));
        Assert.Contains(session.History, e => e is DeskNote n && n.Text.Contains("boot сменился") && n.Text.Contains("⚡1"));
    }

    [Fact]
    public void BackOnlineSameBoot_NotedAsLag()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        var session = _h.Services.Sessions.Create("161432");
        vm.Apply(Snap(S("161432", SessionStatus.Offline)));
        vm.Apply(Snap(S("161432")));
        Assert.Contains(session.History, e => e is DeskNote n && n.Text.Contains("лаг"));
    }

    [Fact]
    public void Tokens_InStatusBar()
    {
        var vm = New();
        Assert.Equal("токены сегодня: 0 · $0.00", vm.Status.TokensText);
        _h.Tokens.Add(new TokenUsage(1000, 500, 0, 0), 0.12m);
        Assert.Equal("токены сегодня: 1.5K · $0.12", vm.Status.TokensText);
    }

    [Fact]
    public async Task SessionState_ShownOnCard()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        vm.StartSessionCommand.Execute(null);
        vm.ActiveChat!.Draft = "привет";
        await vm.ActiveChat.SendCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Working, vm.Items.Single().SessionState);
        Assert.True(vm.Items.Single().HasSession);
    }

    [Fact]
    public void PermissionRequest_RaisesAttention()
    {
        var vm = New();
        _h.Services.Sessions.Create("161432");
        var raised = 0;
        vm.AttentionNeeded += () => raised++;
        _ = _h.Broker.AskAsync("161432", "Bash", System.Text.Json.JsonDocument.Parse("{}").RootElement, null, default);
        Assert.Equal(1, raised);
        Assert.True(vm.HasPendingPermissions);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12_345, "12.3K")]
    [InlineData(2_500_000, "2.5M")]
    public void FormatTokens(long total, string expected)
        => Assert.Equal($"токены сегодня: {expected} · $1.50",
            StatusBarViewModel.FormatTokens(new TokenUsage(total, 0, 0, 0), 1.5m));
}
