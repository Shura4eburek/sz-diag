using SzDiag.Desk.ViewModels.Inspector;
using SzDiag.Kb;

namespace SzDiag.Desk.Tests;

public class JournalTabViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "szkbj-" + Guid.NewGuid().ToString("N"));
    private readonly KbPaths _kb;

    public JournalTabViewModelTests()
    {
        _kb = new KbPaths(_root);
        Directory.CreateDirectory(_kb.SzDir("161432"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ShowsTail()
    {
        File.WriteAllLines(_kb.Journal("161432"), Enumerable.Range(1, 350).Select(i => $"строка {i}"));
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);

        var lines = vm.Text.Split('\n');
        Assert.Equal(JournalTabViewModel.TailLines, lines.Length);
        Assert.Equal("строка 51", lines[0]);
        Assert.Equal("строка 350", lines[^1]);
        Assert.Equal(_kb.Journal("161432"), vm.FilePath);
    }

    [Fact]
    public async Task Missing_SaysSo()
    {
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);
        Assert.StartsWith("журнала пока нет", vm.Text);
    }

    [Fact]
    public async Task FileChanges_Reloaded()
    {
        File.WriteAllText(_kb.Journal("161432"), "первая\n");
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);

        File.AppendAllText(_kb.Journal("161432"), "вторая\n");
        for (var i = 0; i < 50 && !vm.Text.Contains("вторая"); i++) await Task.Delay(100);
        Assert.Contains("вторая", vm.Text);
    }

    [Fact]
    public async Task OpenWhileHubWrites_NoException()
    {
        // Hub дописывает журнал своим потоком — читаем с FileShare.ReadWrite.
        var path = _kb.Journal("161432");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync("идёт запись\n"u8.ToArray());
        await writer.FlushAsync();
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);
        Assert.Contains("идёт запись", vm.Text);
    }
}
