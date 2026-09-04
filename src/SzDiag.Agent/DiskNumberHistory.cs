namespace SzDiag.Agent;

/// <summary>
/// История нумерации <c>\Device\HarddiskN</c> / <c>\Device\RaidPortN</c>. <c>DiskNumber</c>/
/// <c>Adapter</c> плавают от загрузки к загрузке (зависят от порядка подключения) — карта
/// «на сейчас» (<c>Win32_DiskDrive</c>), применённая к АРХИВНОЙ дисковой ошибке, может дать
/// зеркальную привязку.
///
/// Боль (бэклог п.133, СЗ 161346): диски физически поменяли местами 29.07 между 12:11 и
/// 13:57 (PCI bus не плывёт, DiskNumber/Adapter — плывут). Карта «на сейчас», применённая к
/// событиям ДО перестановки, дала обратный вывод: указала на диск из заказа вместо
/// клиентского 500 ГБ, хотя все ошибки были на клиентском. Источник исторической нумерации —
/// <c>Microsoft-Windows-Partition/Diagnostic</c> Id=1006, пишется при каждом подключении
/// диска (несёт <c>DiskNumber</c>, <c>Adapter</c>, модель, серийник, ёмкость). Тот же приём,
/// что и в рецепте <c>tools/recipes/client/disk-number-history.ps1</c>.
///
/// Строго ASCII: тела проб уходят на клиента через EncodedCommand и читаются PowerShell 5.1.
/// </summary>
public static class DiskNumberHistory
{
    public static string PowerShellPrologue() => """
        function Get-DiskNumberHistory {
            $ev = @()
            try {
                $ev = @(Get-WinEvent -FilterHashtable @{ LogName='Microsoft-Windows-Partition/Diagnostic'; Id=1006 } -ErrorAction Stop |
                    Sort-Object TimeCreated)
            } catch { }
            $rows = foreach ($e in $ev) {
                try {
                    $x = [xml]$e.ToXml(); $d = @{}
                    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
                    $diskNum = 0; [void][int]::TryParse("$($d['DiskNumber'])", [ref]$diskNum)
                    [PSCustomObject]@{
                        Time       = $e.TimeCreated
                        DiskNumber = $diskNum
                        Adapter    = $d['Adapter']
                        Model      = ("$($d['Model'])" -replace '\s+', ' ').Trim()
                        Serial     = "$($d['SerialNumber'])".Trim()
                    }
                } catch { }
            }
            return @($rows)
        }

        # Kakoy fizicheskiy disk byl pod nomerom $diskNum v moment $atTime (poslednyaya
        # zapis 1006 DO etogo momenta). $null, esli istoricheskoy zapisi net - podstavlyat
        # SEGODNYASHNYUYU model v etom sluchae NELZYA, ona mozhet okazatsya zerkalnoy (p.133).
        function Resolve-DiskAtTime($history, $diskNum, $atTime) {
            $cand = @($history | Where-Object { $_.DiskNumber -eq $diskNum -and $_.Time -le $atTime } |
                Sort-Object Time -Descending)
            if ($cand.Count -gt 0) { return $cand[0] }
            return $null
        }

        # Smena FIZICHESKOGO diska pod odnim nomerom za okno - sam po sebe diagnosticheskiy
        # fakt (p.133: 'diski pomenyalis mestami' maskiruet defekt slota i rvet statistiku).
        function Write-DiskSlotSwaps($history) {
            $any = $false
            foreach ($g in ($history | Group-Object DiskNumber)) {
                $ordered = @($g.Group | Sort-Object Time)
                for ($i = 1; $i -lt $ordered.Count; $i++) {
                    if ($ordered[$i].Serial -and $ordered[$i - 1].Serial -and $ordered[$i].Serial -ne $ordered[$i - 1].Serial) {
                        $any = $true
                        "{0:dd.MM.yyyy HH:mm} pod Disk{1} vstal DRUGOY fizicheskiy disk: bylo {2} [SN {3}], stalo {4} [SN {5}]" -f `
                            $ordered[$i].Time, $g.Name, $ordered[$i - 1].Model, $ordered[$i - 1].Serial, $ordered[$i].Model, $ordered[$i].Serial
                    }
                }
            }
            if (-not $any) {
                "smeny nomerov diskov za dostupnuyu istoriyu ne obnaruzheno (ili istorii net - kanal Partition/Diagnostic pust)"
            }
        }
        """;
}
