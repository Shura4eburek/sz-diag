using System.Diagnostics;

namespace SzDiag.Claude;

public sealed class ClaudeProcess : IClaudeProcess
{
    public const int StderrLines = 200;

    private readonly object _gate = new();
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private Task? _pump;

    public event Action<string>? OutputLine;
    public event Action<int?>? Exited;

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public IReadOnlyList<string> StderrTail
    {
        get { lock (_gate) return _stderr.ToList(); }
    }

    public void Start(ClaudeLaunch launch) => Start(launch.ToStartInfo());

    /// <summary>Отдельно от <see cref="ClaudeLaunch"/> — чтобы обвязку можно было проверить на
    /// обычной консольной программе.</summary>
    public void Start(ProcessStartInfo psi)
    {
        if (_process is not null) throw new InvalidOperationException("процесс уже запущен");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_gate)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > StderrLines) _stderr.Dequeue();
            }
        };
        p.Start();
        p.BeginErrorReadLine();
        _process = p;
        _pump = Task.Run(() => PumpAsync(p));
    }

    private async Task PumpAsync(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                OutputLine?.Invoke(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // stdout закрылся при kill — дальше просто ждём выхода.
        }
        await p.WaitForExitAsync().ConfigureAwait(false);
        int? code = null;
        try { code = p.ExitCode; }
        catch (InvalidOperationException) { }
        Exited?.Invoke(code);
    }

    public async Task WriteLineAsync(string line)
    {
        var p = _process ?? throw new InvalidOperationException("процесс не запущен");
        await p.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        await p.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(TimeSpan grace)
    {
        var p = _process;
        if (p is null) return;
        try { p.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }

        using var cts = new CancellationTokenSource(grace);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // claude запускает дочерние процессы (bash, MCP-серверы) — убиваем всё дерево.
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (_pump is not null) await _pump.ConfigureAwait(false);
    }
}
