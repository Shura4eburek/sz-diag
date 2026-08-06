using System.Text;

namespace SzDiag.Agent;

/// <summary>Разбор бинарной записи WHEA (UEFI CPER — Common Platform Error Record).
///
/// Боль (бэклог п.68, СЗ 160306): у событий WHEA-Logger с <b>Id=1</b> (fatal) именованных
/// полей нет вовсе — всё лежит в бинарной data section, и сводки «by Error Type / by MCA bank /
/// by APIC ID» выходили пустыми ровно там, где были нужны. На машине с девятью фатальными
/// ошибками мы не узнали ни тип, ни ядро, то есть дискриминатор «CPU vs память vs PCIe» не
/// сработал.
///
/// Здесь — таблицы GUID-ов и генератор PowerShell-функции `Parse-Cper`: она вытаскивает из
/// байтов severity, тип нотификации (MCE/CMC/PCIe/NMI), список секций и, где возможно,
/// APIC ID процессорной секции. Полный разбор MCA-банков намеренно не делаем: 90 % вопросов
/// закрывает «какая это ошибка и на чём», а остальное всё равно смотрится в дампе.
///
/// Структуры (UEFI 2.10, Appendix N):
/// заголовок 128 байт, дальше SectionCount дескрипторов по 72 байта, дальше сами секции.</summary>
public static class CperDecoder
{
    /// <summary>Тип секции CPER (GUID → человеческое имя).</summary>
    public static IReadOnlyDictionary<string, string> SectionTypes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["9876ccad-47b4-4bdb-b65e-16f193c4f3db"] = "Processor Generic",
            ["dc3ea0b0-a144-4797-b95b-53fa242b6e1d"] = "Processor Specific (x86/x64)",
            ["e429faf1-3cb7-11d4-bca7-0080c73c8881"] = "Processor Specific (IA64)",
            ["a5bc1114-6f64-4ede-b863-3e83ed7c83b1"] = "Platform Memory",
            ["61ec04fc-48e6-d813-25c9-8daa44750b12"] = "Platform Memory 2",
            ["d995e954-bbc1-430f-ad91-b44dcb3c6f35"] = "PCI Express",
            ["c5753963-3b84-4095-bf78-eddad3f9c9dd"] = "PCI/PCI-X Bus",
            ["eb5e4685-ca66-4769-b6a2-26068b001326"] = "PCI Component",
            ["81212a96-09ed-4996-9471-8d729c8e69ed"] = "Firmware Error Record",
            ["85183a8b-9c41-429c-939c-5c3c087ca280"] = "DMAr Generic",
        };

    /// <summary>Как ошибка приехала в ОС (GUID → канал). Отвечает на вопрос «это MCE от CPU
    /// или сообщение от PCIe-подсистемы» ещё до разбора секций.</summary>
    public static IReadOnlyDictionary<string, string> NotificationTypes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["e8f56ffe-919c-4cc5-ba88-65abe14913bb"] = "MCE (Machine Check Exception, CPU)",
            ["2dce8bb1-bdd7-450e-b9ad-9cf4ebd4f890"] = "CMC (Corrected Machine Check, CPU)",
            ["4e292f96-d843-4a55-a8c2-d481f27ebeee"] = "CPE (Corrected Platform Error)",
            ["5bad89ff-b7e6-42c9-814a-cf2485d6e98a"] = "NMI",
            ["cf93c01f-1a16-4dfc-b8bc-9c4daf67c104"] = "PCIe error",
            ["cc5263e8-9308-454a-89d0-340bd39bc98e"] = "INIT",
            ["3d61a466-ab40-409a-a698-f362d464b38f"] = "BOOT error",
            ["667dd791-c6b3-4c27-8a6b-0f8e722deb41"] = "DMAr",
        };

    /// <summary>Severity из заголовка CPER. Порядок именно такой (0 — самая тяжёлая).</summary>
    public static IReadOnlyDictionary<int, string> Severity { get; } = new Dictionary<int, string>
    {
        [0] = "Recoverable",
        [1] = "Fatal",
        [2] = "Corrected",
        [3] = "Informational",
    };

    /// <summary>PowerShell-пролог для пробы `whea`: таблицы GUID-ов и функция `Parse-Cper`,
    /// принимающая байты записи и возвращающая объект с severity, каналом, списком секций и
    /// APIC ID. Строго ASCII — см. комментарий к <see cref="DiagnosticProbes"/>.</summary>
    public static string PowerShellPrologue()
    {
        var sb = new StringBuilder();
        sb.Append("$CPER_SECTION=@{");
        sb.AppendJoin(';', SectionTypes.Select(kv => $"'{kv.Key}'='{kv.Value}'"));
        sb.AppendLine("}");
        sb.Append("$CPER_NOTIFY=@{");
        sb.AppendJoin(';', NotificationTypes.Select(kv => $"'{kv.Key}'='{kv.Value}'"));
        sb.AppendLine("}");
        sb.Append("$CPER_SEV=@{");
        sb.AppendJoin(';', Severity.Select(kv => $"'{kv.Key}'='{kv.Value}'"));
        sb.AppendLine("}");
        sb.AppendLine("""
            # UEFI CPER: header 128 bytes, then SectionCount descriptors of 72 bytes each.
            function Parse-Cper($bytes) {
                if ($null -eq $bytes -or $bytes.Length -lt 128) { return $null }
                $sig = [Text.Encoding]::ASCII.GetString($bytes, 0, 4)
                if ($sig -ne 'CPER') { return $null }
                $sectionCount = [BitConverter]::ToUInt16($bytes, 10)
                $sev = [BitConverter]::ToUInt32($bytes, 12)
                # [byte[]] is mandatory: a PowerShell array slice comes back as Object[],
                # and the Guid constructor rejects it with 'Unrecognized Guid format'.
                $creator = (New-Object Guid (,[byte[]]$bytes[32..47])).ToString()
                $notifyGuid = (New-Object Guid (,[byte[]]$bytes[80..95])).ToString()
                $sections = @()
                $apic = $null
                for ($i = 0; $i -lt $sectionCount; $i++) {
                    $d = 128 + $i * 72
                    if ($bytes.Length -lt $d + 72) { break }
                    $off = [BitConverter]::ToUInt32($bytes, $d)
                    $len = [BitConverter]::ToUInt32($bytes, $d + 4)
                    $typeGuid = (New-Object Guid (,[byte[]]$bytes[($d+16)..($d+31)])).ToString()
                    $ssev = [BitConverter]::ToUInt32($bytes, $d + 48)
                    $name = $CPER_SECTION[$typeGuid]
                    if (-not $name) { $name = $typeGuid }
                    $sections += [PSCustomObject]@{
                        Type     = $name
                        Severity = $(if ($CPER_SEV["$ssev"]) { $CPER_SEV["$ssev"] } else { "$ssev" })
                        Length   = $len
                    }
                    # Processor Specific (x86/x64): LocalApicId lies right after ValidBits.
                    if ($typeGuid -eq 'dc3ea0b0-a144-4797-b95b-53fa242b6e1d' -and $bytes.Length -ge $off + 16) {
                        $apic = [BitConverter]::ToUInt64($bytes, $off + 8)
                    }
                    # Processor Generic: ProcessorId sits after the 128-byte brand string.
                    elseif ($typeGuid -eq '9876ccad-47b4-4bdb-b65e-16f193c4f3db' -and $bytes.Length -ge $off + 160) {
                        $apic = [BitConverter]::ToUInt64($bytes, $off + 152)
                    }
                }
                $notify = $CPER_NOTIFY[$notifyGuid]
                if (-not $notify) { $notify = $notifyGuid }
                [PSCustomObject]@{
                    Severity     = $(if ($CPER_SEV["$sev"]) { $CPER_SEV["$sev"] } else { "$sev" })
                    Notification = $notify
                    Creator      = $creator
                    Sections     = $sections
                    ApicId       = $apic
                }
            }
            # WHEA-Logger keeps the raw record in one of the event properties; pick the longest
            # byte[] - named fields are absent on Id=1 (backlog p.68).
            function Get-CperBytes($e) {
                $best = $null
                foreach ($p in $e.Properties) {
                    $v = $p.Value
                    if ($v -is [byte[]] -and $v.Length -ge 128) {
                        if ($null -eq $best -or $v.Length -gt $best.Length) { $best = $v }
                    }
                }
                return $best
            }
            """);
        return sb.ToString();
    }
}
