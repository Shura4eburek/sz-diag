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
        var p = new Process { StartInfo = psi };
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
        _handleSignaled = WaitForHandleAsync(p);
        _pump = Task.Run(() => PumpAsync(p));
        _exit = RaiseExitedAsync(p);
    }

    /// <summary>Сколько после выхода процесса ждать, пока stdout дочитается (итоговый result).
    /// EOF ждать нельзя: пайп может держать осиротевший внук — фоновый процесс из тулзы, чей
    /// родитель уже умер (ревью I-4, тест OrphanHoldingPipes).</summary>
    public static readonly TimeSpan PumpDrain = TimeSpan.FromSeconds(2);

    private Task? _handleSignaled;
    private Task? _exit;
    private RegisteredWaitHandle? _registration;
    private ManualResetEvent? _waitHandle;

    /// <summary>Ожидание дескриптора процесса, а не WaitForExitAsync: тот при асинхронном чтении
    /// stderr ждёт ещё и EOF пайпов — ровно то, что держит осиротевший внук.</summary>
    private Task WaitForHandleAsync(Process p)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitHandle = new ManualResetEvent(false)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(p.Handle, ownsHandle: false),
        };
        _registration = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => tcs.TrySetResult(),
            null, Timeout.Infinite, executeOnlyOnce: true);
        return tcs.Task;
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
            // stdout закрыт (kill или Dispose после выхода) — читать больше нечего.
        }
    }

    private async Task RaiseExitedAsync(Process p)
    {
        await _handleSignaled!.ConfigureAwait(false);
        await Task.WhenAny(_pump!, Task.Delay(PumpDrain)).ConfigureAwait(false);
        int? code = null;
        try { code = p.ExitCode; }
        catch (InvalidOperationException) { }
        Exited?.Invoke(code);

        _registration?.Unregister(null);
        _waitHandle?.Dispose();
        // Dispose закрывает и stdout: если его держит осиротевший внук, насос выходит по исключению.
        p.Dispose();
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
        if (p is null || _handleSignaled is null || _exit is null) return;
        try { p.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }

        if (await Task.WhenAny(_handleSignaled, Task.Delay(grace)).ConfigureAwait(false) != _handleSignaled)
        {
            // claude запускает дочерние процессы (bash, MCP-серверы) — убиваем всё дерево.
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        // Exited поднимается не позже чем через PumpDrain после выхода; с запасом на kill.
        await Task.WhenAny(_exit, Task.Delay(PumpDrain + TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }
}
