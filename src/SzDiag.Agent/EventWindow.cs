namespace SzDiag.Agent;

/// <summary>
/// Единое окно поиска по журналу для read-only проб (по умолчанию 30 дней) + явная печать
/// глубины журнала, чтобы «пусто» не читалось как «дефекта нет».
///
/// Боль (бэклог п.123, СЗ 161346): машина приехала в сервис через две недели после последнего
/// вырубона (дефект гулял 25-29.07, СЗ смотрели 10.08). Рецепт с зашитым в тело `$Days = 14`
/// вернул ПУСТОТУ там, где в журнале лежало 25 событий Kernel-Power 41 — на секунду это
/// читалось как «дефект не воспроизводится», хотя было ровно наоборот. Окно унифицировано на
/// 30 дней везде, где встречается (`kp41-detail.ps1`, `whea-storage-detail.ps1`, пробы
/// `events`/`reboots`/`whea`), и теперь печатается явно вместе с датой самого старого события
/// в журнале `System` — тогда «пусто» отличимо от «журнал не достаёт так далеко».
///
/// Строго ASCII: тела проб уходят на клиента через EncodedCommand и читаются PowerShell 5.1.
/// </summary>
public static class EventWindow
{
    /// <summary>PowerShell-пролог: <c>$SZ_EVENT_WINDOW_DAYS</c> (30), <c>Write-EventWindowNote</c>
    /// (окно поиска + глубина журнала — для проб, которые режут историю по дням) и
    /// <c>Write-JournalDepthNote</c> (только глубина — для проб, читающих полную историю без
    /// окна, напр. <c>reboots</c>/<c>whea</c>: там «пусто» тоже надо уметь отличить от
    /// «журнал короче, чем кажется»).</summary>
    public static string PowerShellPrologue() => """
        $SZ_EVENT_WINDOW_DAYS = 30

        function Get-OldestSystemEvent {
            try { return Get-WinEvent -LogName System -Oldest -MaxEvents 1 -ErrorAction Stop } catch { return $null }
        }

        function Write-EventWindowNote {
            $winSince = (Get-Date).AddDays(-$SZ_EVENT_WINDOW_DAYS)
            $oldest = Get-OldestSystemEvent
            if ($oldest) {
                "Okno poiska: {0} dney (s {1:yyyy-MM-dd}). Samoe staroe sobytie v zhurnale System: {2:yyyy-MM-dd HH:mm}." -f `
                    $SZ_EVENT_WINDOW_DAYS, $winSince, $oldest.TimeCreated
                if ($oldest.TimeCreated -gt $winSince) {
                    "VNIMANIE: zhurnal NE DOSTAET do nachala okna poiska - 'pusto' zdes mozhet znachit 'zhurnal korotkiy', a ne 'defekta net'."
                }
            } else {
                "Okno poiska: {0} dney (s {1:yyyy-MM-dd}). Glubinu zhurnala System opredelit ne udalos (Get-WinEvent -Oldest ne otvetil)." -f `
                    $SZ_EVENT_WINDOW_DAYS, $winSince
            }
        }

        function Write-JournalDepthNote {
            $oldest = Get-OldestSystemEvent
            if ($oldest) {
                "Glubina zhurnala System: dostupen s {0:yyyy-MM-dd HH:mm} (istoriya vyshe - 'FULL HISTORY' v predelah etoy glubiny, ne 'vsegda')." -f $oldest.TimeCreated
            } else {
                "Glubinu zhurnala System opredelit ne udalos (Get-WinEvent -Oldest ne otvetil)."
            }
        }
        """;
}
