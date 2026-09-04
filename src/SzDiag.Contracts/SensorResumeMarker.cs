using System.Text.Json;

namespace SzDiag.Contracts;

/// <summary>Маркер «приборный захват активен для СЗ N» на диске клиента — обычный файл,
/// который переживает перезагрузку (как маркер заморозки Windows Update, см.
/// <see cref="WindowsUpdateFreeze"/>), в отличие от самого наблюдателя (обычный дочерний
/// процесс агента) и его CSV.
///
/// Боль (бэклог п.147, СЗ 160705): после hard-off и подъёма машины `lhmmon` не стартовал
/// сам — задача создавалась с `/sc once`, автозапуска у неё нет. Агент при этом
/// восстанавливается (`--resume` + автостарт-таск), то есть управление возвращается, а
/// приборка — нет. Маркер даёт агенту при `--resume` всё, что нужно, чтобы поднять
/// наблюдатель заново, не спрашивая hub.</summary>
public static class SensorResumeMarker
{
    public const string MarkerPath = @"C:\ProgramData\szdiag\sensors\resume-marker.json";

    public sealed record Info(string Sz, int IntervalSeconds, int Minutes);

    /// <summary>PowerShell-фрагмент, который кладёт/обновляет маркер — вызывается той же
    /// командой exec, что поднимает наблюдатель (`szcli sensors start`), поэтому маркер живёт
    /// ровно пока живёт захват.</summary>
    public static string BuildWriteScript(string sz, int intervalSeconds, int minutes)
    {
        var pathEscaped = MarkerPath.Replace("'", "''");
        var json = JsonSerializer.Serialize(new Info(sz, intervalSeconds, minutes)).Replace("'", "''");
        return $"New-Item -ItemType Directory -Force -Path (Split-Path '{pathEscaped}') | Out-Null\n" +
               $"Set-Content -Path '{pathEscaped}' -Value '{json}' -Encoding utf8";
    }

    /// <summary>PowerShell-фрагмент удаления маркера — вызывается на `szcli sensors stop`,
    /// иначе агент после следующего же ребута поднимет наблюдатель, который уже сознательно
    /// остановили.</summary>
    public static string BuildRemoveScript()
    {
        var pathEscaped = MarkerPath.Replace("'", "''");
        return $"Remove-Item -Path '{pathEscaped}' -Force -ErrorAction SilentlyContinue";
    }

    /// <summary>Разбор содержимого маркера. Битый/чужой формат — null, а не исключение:
    /// маркер пишется сторонним процессом (клиентом), доверять ему буквально нельзя.</summary>
    public static Info? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Info>(json); }
        catch (JsonException) { return null; }
    }
}
