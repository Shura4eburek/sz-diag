using System.Net;
using System.Net.Sockets;

namespace SzDiag.Agent.Zt;

/// <summary>Проброс ZT:<c>port</c> → <c>127.0.0.1:port</c> (T043): sshd слушает локально, а hub
/// приходит к клиенту по адресу в сервисной сети. Вместо quick tunnel, без внешнего процесса.</summary>
public sealed class ZtSshForwarder : IDisposable
{
    private readonly IZtListener _listener;
    private readonly int _targetPort;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDisposable> _active = [];
    private readonly Thread _acceptor;

    public ZtSshForwarder(IZtListener listener, int targetPort, Action<string>? log = null)
    {
        _listener = listener;
        _targetPort = targetPort;
        _log = log;
        _acceptor = new Thread(AcceptLoop) { IsBackground = true, Name = "zt-ssh-accept" };
        _acceptor.Start();
    }

    private void AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            Stream zt;
            try { zt = _listener.Accept(); }
            catch (Exception ex)
            {
                if (!_stop.IsCancellationRequested) _log?.Invoke($"zt-ssh: accept: {ex.Message}");
                return;
            }
            _ = Task.Run(() => PumpAsync(zt));
        }
    }

    private async Task PumpAsync(Stream zt)
    {
        var tcp = new TcpClient { NoDelay = true };
        lock (_active) { _active.Add(zt); _active.Add(tcp); }
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _targetPort, _stop.Token).ConfigureAwait(false);
            var local = tcp.GetStream();
            var up = zt.CopyToAsync(local, 64 * 1024, _stop.Token);
            var down = local.CopyToAsync(zt, 64 * 1024, _stop.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            _log?.Invoke($"zt-ssh: {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
        finally
        {
            lock (_active) { _active.Remove(zt); _active.Remove(tcp); }
            try { zt.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
        }
    }

    public int ActiveConnections { get { lock (_active) return _active.Count / 2; } }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _listener.Dispose(); } catch { }
        IDisposable[] snapshot;
        lock (_active) snapshot = [.. _active];
        foreach (var d in snapshot) try { d.Dispose(); } catch { }
    }
}
