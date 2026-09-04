namespace SzDiag.Cli;

/// <summary>Что удалось узнать одной командой `szcli alive` (бэклог п.202, СЗ 161972): раньше
/// вердикт «вырубилась или висит» держался на шести прогонах `szcli list`, `Test-NetConnection`,
/// `arp -a` руками, а знание «нет ARP-записи = питания нет» жило только в голове инженера.</summary>
public sealed record AliveSignals(
    TimeSpan? HeartbeatAge,
    bool ArpFound,
    bool IcmpOk,
    bool AnyTcpOpen)
{
    /// <summary>Heartbeat свежее этого порога считается живым без дальнейших проб (совпадает
    /// с дефолтным `Hub.HeartbeatTimeout`).</summary>
    public static readonly TimeSpan FreshThreshold = TimeSpan.FromSeconds(60);
}

public static class AliveVerdict
{
    public static string Describe(AliveSignals s)
    {
        if (s.HeartbeatAge is { } age && age <= AliveSignals.FreshThreshold)
            return "heartbeat свежий — машина точно жива.";

        if (!s.ArpFound)
            return "молчит на канальном уровне (нет ARP-записи) — похоже, питания нет, а не «висит ОС».";

        if (s.AnyTcpOpen)
            return "сеть отвечает (порт открыт), но heartbeat молчит — вероятно жива и задавлена нагрузкой.";

        if (s.IcmpOk)
            return "отвечает на ping, но ни один порт и heartbeat молчат — состояние неоднозначное, проверить руками.";

        return "есть ARP-запись, но сеть не отвечает ни на ping, ни на порты — ОС зависла или сетевой стек не поднялся.";
    }
}
