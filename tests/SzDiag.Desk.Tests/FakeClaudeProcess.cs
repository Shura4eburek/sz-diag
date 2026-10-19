using SzDiag.Claude;

namespace SzDiag.Desk.Tests;

public sealed class FakeClaudeProcess : IClaudeProcess
{
    public List<string> Written { get; } = new();
    public List<string> Stderr { get; } = new();
    public ClaudeLaunch? Launch { get; private set; }
    public bool Stopped { get; private set; }
    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> StderrTail => Stderr;

    public event Action<string>? OutputLine;
    public event Action<int?>? Exited;

    public void Start(ClaudeLaunch launch)
    {
        Launch = launch;
        IsRunning = true;
    }

    public Task WriteLineAsync(string line)
    {
        Written.Add(line);
        return Task.CompletedTask;
    }

    public Task StopAsync(TimeSpan grace)
    {
        Stopped = true;
        if (IsRunning) Exit(0);
        return Task.CompletedTask;
    }

    public void Emit(string line) => OutputLine?.Invoke(line);

    public void Exit(int? code)
    {
        IsRunning = false;
        Exited?.Invoke(code);
    }
}
