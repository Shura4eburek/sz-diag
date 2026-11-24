namespace SzDiag.Agent.Zt;

/// <summary>Что шаг 11 <c>Open</c> записывает в <see cref="RevertState"/>.</summary>
public sealed record ZtAccessInfo(string NodeId, string Address, string Network, string StoragePath);

/// <summary>Шаг 11 доступа — SSH через сервисную ZT-сеть (T045). Абстракция ради тестов
/// <see cref="WindowsSystemAccessManager"/>, как <see cref="IAccessTunnel"/> для quick tunnel.</summary>
public interface IZtAccess
{
    /// <summary>Поднять проброс ZT:<paramref name="sshPort"/> → локальный sshd. null — ноды ZT в
    /// процессе нет (hub найден по LAN/туннелю): шаг просто не применяется.</summary>
    ZtAccessInfo? Start(int sshPort);

    /// <summary>Снять проброс и стереть storage ноды. Пока нода жива (через неё ещё уйдёт отчёт
    /// об откате), стирание откладывается до её остановки.</summary>
    void Stop(string storagePath);
}

public sealed class ZtAccess(ZtHubSession session, Action<string>? log = null) : IZtAccess
{
    private ZtSshForwarder? _forwarder;

    public ZtAccessInfo? Start(int sshPort)
    {
        var node = session.Node;
        if (node is null) return null;
        _forwarder?.Dispose();
        _forwarder = new ZtSshForwarder(node.Listen(sshPort), sshPort, log);
        return new ZtAccessInfo(node.NodeId, node.Address.ToString(), node.NetworkId, node.StoragePath);
    }

    public void Stop(string storagePath)
    {
        _forwarder?.Dispose();
        _forwarder = null;
        if (session.Node is not null && string.Equals(session.StoragePath, storagePath, StringComparison.OrdinalIgnoreCase))
            session.WipeOnStop();
        else
            ZtStorage.Wipe(storagePath);
    }
}
