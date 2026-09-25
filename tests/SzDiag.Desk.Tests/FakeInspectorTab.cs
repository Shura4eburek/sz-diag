using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public sealed class FakeInspectorTab(TimeSpan? interval, bool onReboot = false) : IInspectorTab
{
    public string Title => "fake";
    public TimeSpan? Interval => interval;
    public bool RefreshOnReboot => onReboot;
    public List<string> Refreshed { get; } = new();
    public int Cleared { get; private set; }
    public TaskCompletionSource? Gate { get; set; }

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        Refreshed.Add(sz);
        if (Gate is not null) await Gate.Task;
    }

    public void Clear() => Cleared++;
}
