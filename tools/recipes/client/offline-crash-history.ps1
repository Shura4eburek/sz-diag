$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
$sys = 'W:\Windows\System32\winevt\Logs\System.evtx'
$all = @(Get-WinEvent -Path $sys -ErrorAction SilentlyContinue)

function Show-Data($e) {
    # В PE сообщения не резолвятся (нет манифестов провайдера) - читаем EventData из XML.
    $x = [xml]$e.ToXml()
    $pairs = @()
    foreach ($d in $x.Event.EventData.Data) {
        $n = $d.Name; $v = $d.'#text'
        if ($v -and $v -ne '0' -and $n) { $pairs += ($n + '=' + $v) }
    }
    ($pairs -join ' ')
}

'=== Kernel-Power 41: детали последних 12 вырубонов ==='
foreach ($e in (@($all | Where-Object { $_.Id -eq 41 } | Sort-Object TimeCreated) | Select-Object -Last 12)) {
    $t = $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
    ('  ' + $t + ' UTC  ' + (Show-Data $e))
}
''
'=== WHEA-Logger (все) ==='
foreach ($e in (@($all | Where-Object { $_.ProviderName -eq 'Microsoft-Windows-WHEA-Logger' } | Sort-Object TimeCreated))) {
    $t = $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
    ('  ' + $t + ' UTC  id=' + $e.Id + '  ' + (Show-Data $e))
}
''
'=== nvlddmkm / Display (все) ==='
foreach ($e in (@($all | Where-Object { $_.ProviderName -in @('nvlddmkm','Display','Microsoft-Windows-DxgKrnl') } | Sort-Object TimeCreated))) {
    $t = $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
    $x = [xml]$e.ToXml()
    $raw = ($x.Event.EventData.InnerText -replace '\s+',' ')
    if ($raw.Length -gt 120) { $raw = $raw.Substring(0,120) }
    ('  ' + $t + ' UTC  id=' + $e.Id + ' [' + $e.ProviderName + '] lvl=' + $e.LevelDisplayName + ' ' + $raw)
}
