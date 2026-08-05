using System.Text;

namespace SzDiag.Agent;

/// <summary>Расшифровка стоп-кодов BSOD. Windows кладёт в Kernel-Power 41 поле
/// <c>BugcheckCode</c> в <b>десятичном</b> виде (<c>190</c>, <c>340</c>), а вся литература
/// и <c>!analyze -v</c> оперируют hex-именами (<c>0xBE ATTEMPTED_WRITE_TO_READONLY_MEMORY</c>).
/// Перевод руками делался на каждом разборе — таблица закрывает это в отчёте.
///
/// Таблица не претендует на полноту: здесь коды, которые реально встречаются на заявках
/// сервиса (память/питание/диск/GPU). Незнакомый код печатается как hex без имени —
/// это лучше, чем decimal.</summary>
public static class BugcheckCodes
{
    /// <summary>Код стоп-ошибки → официальное имя (как в WinDbg).</summary>
    public static IReadOnlyDictionary<uint, string> Names { get; } = new Dictionary<uint, string>
    {
        [0x0A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x18] = "REFERENCE_BY_POINTER",
        [0x19] = "BAD_POOL_HEADER",
        [0x1A] = "MEMORY_MANAGEMENT",
        [0x1E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x24] = "NTFS_FILE_SYSTEM",
        [0x3B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x44] = "MULTIPLE_IRP_COMPLETE_REQUESTS",
        [0x4E] = "PFN_LIST_CORRUPT",
        [0x50] = "PAGE_FAULT_IN_NONPAGED_AREA",
        [0x51] = "REGISTRY_ERROR",
        [0x5C] = "HAL_INITIALIZATION_FAILED",
        [0x7A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x7E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        [0x7F] = "UNEXPECTED_KERNEL_MODE_TRAP",
        [0x9F] = "DRIVER_POWER_STATE_FAILURE",
        [0xA0] = "INTERNAL_POWER_ERROR",
        [0xBE] = "ATTEMPTED_WRITE_TO_READONLY_MEMORY",
        [0xC2] = "BAD_POOL_CALLER",
        [0xC4] = "DRIVER_VERIFIER_DETECTED_VIOLATION",
        [0xC5] = "DRIVER_CORRUPTED_EXPOOL",
        [0xCA] = "PNP_DETECTED_FATAL_ERROR",
        [0xD1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0xEF] = "CRITICAL_PROCESS_DIED",
        [0xF4] = "CRITICAL_OBJECT_TERMINATION",
        [0xF7] = "DRIVER_OVERRAN_STACK_BUFFER",
        [0xFC] = "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        [0x101] = "CLOCK_WATCHDOG_TIMEOUT",
        [0x109] = "CRITICAL_STRUCTURE_CORRUPTION",
        [0x113] = "VIDEO_DXGKRNL_FATAL_ERROR",
        [0x116] = "VIDEO_TDR_ERROR",
        [0x117] = "VIDEO_TDR_TIMEOUT_DETECTED",
        [0x119] = "VIDEO_SCHEDULER_INTERNAL_ERROR",
        [0x124] = "WHEA_UNCORRECTABLE_ERROR",
        [0x133] = "DPC_WATCHDOG_VIOLATION",
        [0x139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x13A] = "KERNEL_MODE_HEAP_CORRUPTION",
        [0x141] = "VIDEO_ENGINE_TIMEOUT_DETECTED",
        [0x144] = "BUGCODE_USB3_DRIVER",
        [0x154] = "UNEXPECTED_STORE_EXCEPTION",
        [0x18B] = "SECURE_KERNEL_ERROR",
        [0x193] = "VIDEO_DXGKRNL_LIVEDUMP",
    };

    /// <summary>«0xBE ATTEMPTED_WRITE_TO_READONLY_MEMORY» — или просто hex, если код незнаком.
    /// Ноль означает «BSOD не было» (машина оборвалась жёстко), это отдельный случай.</summary>
    public static string Format(uint code)
    {
        var hex = "0x" + code.ToString("X");
        return Names.TryGetValue(code, out var name) ? $"{hex} {name}" : hex;
    }

    /// <summary>Тот же справочник в виде PowerShell-пролога для диагностических проб:
    /// хеш <c>$BUGCHECK</c> (ключ — decimal-строка, ровно как поле события) и функция
    /// <c>Fmt-Bug</c>. Генерится из <see cref="Names"/>, чтобы таблица жила в одном месте
    /// и покрывалась тестами, а не дублировалась строкой внутри скрипта.</summary>
    public static string PowerShellPrologue()
    {
        var sb = new StringBuilder();
        sb.Append("$BUGCHECK=@{");
        sb.AppendJoin(';', Names.OrderBy(kv => kv.Key)
            .Select(kv => $"'{kv.Key}'='{kv.Value}'"));
        sb.AppendLine("}");
        sb.AppendLine("""
            function Fmt-Bug($c) {
                if ($null -eq $c -or "$c" -eq '') { return '' }
                $n = 0
                if (-not [int]::TryParse("$c", [ref]$n)) { return "$c" }
                if ($n -eq 0) { return '0x0 (no BSOD - hard power loss)' }
                $h = '0x{0:X}' -f $n
                $name = $BUGCHECK["$n"]
                if ($name) { "$h $name" } else { "$h (unknown code)" }
            }
            """);
        return sb.ToString();
    }
}
