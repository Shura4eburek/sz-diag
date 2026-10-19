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
        Assert.Equal("GPU паспорт 161432", vm.Sections.Single().Rows.Single().Value);
        Assert.Equal(new[] { "hw", "passport", "161432" }, szcli.Calls.Single());

        vm.Clear();
        await vm.RefreshAsync("161432", default);
        Assert.Single(szcli.Calls);                 // тот же паспорт — из кэша

        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(2, szcli.Calls.Count);         // «обновить» — снимает заново
    }

    [Fact]
    public void Parse_SectionsKeyValue_StderrApart()
    {
        // 160176: сырой вывод в 290 px переносился посреди «ключ : значение» — нечитаемо.
        var p = HardwareTabViewModel.Parse(
            "=== Видеокарта ===\nНазвание           : NVIDIA GeForce RTX 5060\nSUBSYS             : SUBSYS_53711462\n\n" +
            "=== Ошибки видеодрайвера ===\nсобытий TDR/падений видеодрайвера нет\nstderr: Cannot convert value");
        Assert.Equal(new[] { "Видеокарта", "Ошибки видеодрайвера" }, p.Sections.Select(s => s.Title));
        Assert.Equal(("Название", "NVIDIA GeForce RTX 5060"), (p.Sections[0].Rows[0].Key, p.Sections[0].Rows[0].Value));
        Assert.Equal(("", "событий TDR/падений видеодрайвера нет"), (p.Sections[1].Rows[0].Key, p.Sections[1].Rows[0].Value));
        Assert.Equal("Cannot convert value", p.Stderr);
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
