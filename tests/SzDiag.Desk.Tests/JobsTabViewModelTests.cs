using SzDiag.Contracts;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class JobsTabViewModelTests
{
    // Ровно так список отдаёт агент (ExecCommandHandler.ListJobs).
    private const string List =
        "a1b2c3  выполняется, старт 25.09 12:00:01, вывода 1024 б\n    & C:\\OCCT\\OCCTCmd.exe test\n" +
        "d4e5f6  завершена (exit 0), старт 25.09 11:00:00, вывода 12 б";

    private static ExecJobStatus Listing(string text) => new("r", "*", false, null, text, DateTimeOffset.Now, 0);

    [Fact]
    public void Parse_RowsWithScriptPreview()
    {
        var rows = JobsTabViewModel.Parse(List);
        Assert.Equal(new[] { "a1b2c3", "d4e5f6" }, rows.Select(r => r.Id));
        Assert.StartsWith("выполняется", rows[0].State);
        Assert.Equal("& C:\\OCCT\\OCCTCmd.exe test", rows[0].Script);
        Assert.Null(rows[1].Script);
        Assert.Empty(JobsTabViewModel.Parse("фоновых задач нет"));
    }

    [Fact]
    public async Task Refresh_ListsJobs_SelectedShowsOutput()
    {
        var api = new FakeHubApi
        {
            Jobs = _ => Listing(List),
            JobStatus = (_, id) => new ExecJobStatus("r", id, true, null, $"вывод {id}", DateTimeOffset.Now, 10),
        };
        var vm = new JobsTabViewModel(api);
        await vm.RefreshAsync("161432", default);
        Assert.Equal(2, vm.Rows.Count);

        vm.Selected = vm.Rows[0];
        await vm.RefreshAsync("161432", default);
        Assert.Equal("вывод a1b2c3", vm.Output);
        Assert.Equal("a1b2c3", vm.Selected!.Id);   // выбор переживает обновление списка
    }

    [Fact]
    public async Task NoJobs_SaysSo()
    {
        var vm = new JobsTabViewModel(new FakeHubApi { Jobs = _ => Listing("фоновых задач нет") });
        await vm.RefreshAsync("161432", default);
        Assert.Empty(vm.Rows);
        Assert.Equal("фоновых задач нет", vm.Message);
    }

    [Fact]
    public async Task Timeout_KeepsRowsAndSaysSo()
    {
        // Под полной нагрузкой exec-канал глохнет: это не вырубон и не повод очищать список.
        var loaded = false;
        var api = new FakeHubApi
        {
            Jobs = _ => loaded ? throw new TimeoutException("нет ответа") : Listing(List),
        };
        var vm = new JobsTabViewModel(api);
        await vm.RefreshAsync("161432", default);
        loaded = true;
        await vm.RefreshAsync("161432", default);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains("агент не ответил", vm.Message);
    }

    [Fact]
    public async Task ClientTimeout_TaskCanceled_SaysAgentSilent()
    {
        // Живая проверка: клиент hub рвёт запрос своим 30-секундным таймаутом — это
        // TaskCanceledException, а не TimeoutException, и вкладка молча оставалась пустой.
        var vm = new JobsTabViewModel(new FakeHubApi { Jobs = _ => throw new TaskCanceledException() });
        await vm.RefreshAsync("161432", default);
        Assert.Contains("агент не ответил", vm.Message);
    }

    [Fact]
    public async Task SzOffline_SaysSo()
    {
        var vm = new JobsTabViewModel(new FakeHubApi { Jobs = _ => null });
        await vm.RefreshAsync("161432", default);
        Assert.Equal("СЗ не на связи", vm.Message);
    }
}
