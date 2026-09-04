namespace SzDiag.Cli;

/// <summary>Разбор вывода `arp -a`: единственный дискриминатор «выключена физически» против
/// «висит ОС с живой сетью» (бэклог п.202, СЗ 161972) — отсутствие ARP-записи значит, что
/// машина не отвечает на канальном уровне вообще, а живая запись при мёртвом heartbeat
/// говорит, что сетевая карта жива и дело в ОС/нагрузке.</summary>
public static class ArpTableParser
{
    /// <summary>Есть ли живая (не все нули) запись для этого IP в выводе `arp -a`.</summary>
    public static bool HasEntry(string? arpOutput, string ip)
    {
        foreach (var raw in (arpOutput ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0] != ip) continue;

            var mac = parts[1].Replace("-", "").Replace(":", "");
            if (mac.Length == 0) continue;
            if (mac.TrimStart('0').Length == 0) continue; // 00-00-00-00-00-00 — записи фактически нет
            return true;
        }
        return false;
    }
}
