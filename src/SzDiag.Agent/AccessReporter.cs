using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Сборка отчёта «чем машина доступна» для hub. Вынесено из Program.cs отдельно,
/// чтобы правило выбора режима было проверяемым: ошибка здесь означает, что `szcli target`
/// печатает строку, которая не сработает.</summary>
public static class AccessReporter
{
    /// <param name="sz">Номер СЗ, под которым агент реально зарегистрирован. Берём из
    /// аргумента, а не из <paramref name="state"/>: состояние могло остаться от прошлой
    /// заявки.</param>
    /// <param name="foundHubByBroadcast">Hub найден UDP-broadcast'ом, то есть машина в одной
    /// сети с боксом — идём напрямую, без Cloudflare.</param>
    public static AccessReportRequest BuildReport(RevertState state, string sz,
        bool foundHubByBroadcast)
    {
        var туннельЖив = state.StartedQuickTunnel
                         && !string.IsNullOrWhiteSpace(state.QuickTunnelHost);

        // Туннельным режим называем, только когда имя реально есть: иначе target напечатает
        // ProxyCommand в никуда вместо честного «SSH недоступен».
        if (foundHubByBroadcast || !туннельЖив)
            return new AccessReportRequest(sz, null, AccessMode.Direct, state.SshHostPublicKey);

        return new AccessReportRequest(sz, state.QuickTunnelHost, AccessMode.Tunnel,
            state.SshHostPublicKey);
    }
}
