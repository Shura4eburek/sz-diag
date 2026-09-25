using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Kb;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Журнал СЗ (`kb/СЗ/&lt;сз&gt;/журнал.md`) — хвост с диска. Туда hub пишет команды CLI
/// и события машины, а `szcli note` — ручные шаги. Обновляется по изменению файла, а не по таймеру.</summary>
public sealed partial class JournalTabViewModel(KbPaths kb, Action<Action> ui) : ObservableObject, IInspectorTab, IDisposable
{
    public const int TailLines = 300;

    private FileSystemWatcher? _watcher;
    private string? _sz;

    public string Title => "Журнал";
    public TimeSpan? Interval => null;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _filePath = "";

    public Task RefreshAsync(string sz, CancellationToken ct)
    {
        if (_sz != sz)
        {
            _sz = sz;
            Watch(sz);
        }
        Reload();
        return Task.CompletedTask;
    }

    public void Reload()
    {
        if (_sz is not { } sz) return;
        var path = kb.Journal(sz);
        FilePath = path;
        if (!File.Exists(path))
        {
            Text = "журнала пока нет — он заводится с первой командой szcli по СЗ или заметкой";
            return;
        }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
            Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - TailLines)));
        }
        catch (IOException ex)
        {
            Text = $"журнал не прочитался: {ex.Message}";
        }
    }

    private void Watch(string sz)
    {
        _watcher?.Dispose();
        _watcher = null;
        var dir = kb.SzDir(sz);
        if (!Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(kb.Journal(sz)))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => ui(Reload);
        _watcher.Created += (_, _) => ui(Reload);
    }

    public void Clear()
    {
        _watcher?.Dispose();
        _watcher = null;
        _sz = null;
        Text = "";
        FilePath = "";
    }

    public void Dispose() => _watcher?.Dispose();
}
