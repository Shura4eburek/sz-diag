namespace SzDiag.Cli;

/// <param name="Name">Служба/процесс, который лечение обычно отключает.</param>
/// <param name="Controls">Что ещё этот софт обслуживает на типовой сборке — теряется вместе
/// с отключением, даже если приборно это выглядит как чистое лечение.</param>
public sealed record DualPurposeSoftwareEntry(string Name, string Controls);

/// <summary>Список известных «двойных» демонов — вендорского софта, который лечит один
/// симптом (нагрев, троттлинг), но заодно обслуживает функцию, за которую клиент платил.
///
/// Боль (СЗ 161190, бэклог п.172): `LEDKeeper2` (MSI Mystic Light) держал видеокарту в P0 —
/// отключение вылечило троттлинг идеально (P8, вентилятор 0 %), но на этой сборке к плате
/// была подключена и подсветка башни ID-Cooling ARGB, и корпусные вентиляторы — вместе с
/// шумом ушла вся подсветка. Клиент такого «ремонта» не заказывал, и это едва не уехало в
/// отчёт как «неисправность устранена». Приборно цену решения не видно никак: ARGB-ленты не
/// отдают статуса в ОС.</summary>
public static class DualPurposeSoftware
{
    public static readonly IReadOnlyList<DualPurposeSoftwareEntry> Known = new[]
    {
        new DualPurposeSoftwareEntry("LEDKeeper2 / MSI Mystic Light",
            "RGB-подсветка — в т.ч. корпусные вентиляторы и ARGB-ленты, подключённые к плате"),
        new DualPurposeSoftwareEntry("MSI Center (MSI_Center_Service / MSI_Case_Service)",
            "фан-профили корпуса и подсветка — родительские службы для LEDKeeper2"),
        new DualPurposeSoftwareEntry("Armoury Crate / Aura Sync (ASUS)",
            "RGB-подсветка платы/периферии и фан-профили Q-Fan"),
        new DualPurposeSoftwareEntry("iCUE (Corsair)",
            "RGB-подсветка, фан-контроль (Commander/Lighting Node) и профили макросов периферии"),
        new DualPurposeSoftwareEntry("SignalRGB",
            "RGB-подсветка сторонних устройств (может быть единственным драйвером синхронизации)"),
        new DualPurposeSoftwareEntry("Synapse (Razer) / G HUB (Logitech)",
            "макросы и профили периферии — назначенные кнопки перестанут работать"),
        new DualPurposeSoftwareEntry("Fan Xpert (ASUS) / SIV (Gigabyte) / Command Center (MSI)",
            "пользовательские кривые вентиляторов — после отключения система откатится на BIOS-профиль"),
    };
}
