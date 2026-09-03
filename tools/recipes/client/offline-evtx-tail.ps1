$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Грабля 162938: сортировка журнала по TimeCreated даёт кашу, если у машины сбиты часы
# (RTC/BIOS-дата). Истинный порядок записи - RecordId, он монотонный. Хвост по RecordId
# показывает и реальную последовательность, и сам факт скачка часов (время назад/вперёд
# между соседними записями). Плюс сразу: BSOD-события (1001/BugCheck), minidump'ы и то,
# какое время сейчас у самого PE - иначе все выводы о датах строятся на песке.
$sys = 'W:\Windows\System32\winevt\Logs\System.evtx'
$app = 'W:\Windows\System32\winevt\Logs\Application.evtx'

('время PE сейчас (local): ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  зона: ' + (Get-TimeZone).Id)
('время PE сейчас (UTC)  : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))
''

$all = @(Get-WinEvent -Path $sys -ErrorAction SilentlyContinue)
('всего записей в System.evtx: ' + $all.Count)
$byRec = @($all | Sort-Object RecordId)
''
'=== последние 40 записей в порядке ЗАПИСИ (RecordId) ==='
'rec      | время события (UTC)  | id   | провайдер'
foreach ($e in ($byRec | Select-Object -Last 40)) {
    ('{0,-8} | {1} | {2,-4} | {3}' -f $e.RecordId, $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), $e.Id, $e.ProviderName)
}
''
'=== скачки часов (время соседних записей идёт назад > 1 мин) ==='
$prev = $null
foreach ($e in $byRec) {
    if ($prev -and ($e.TimeCreated - $prev.TimeCreated).TotalMinutes -lt -1) {
        ('  rec {0} {1} -> rec {2} {3}  (назад на {4:N0} мин)' -f $prev.RecordId,
            $prev.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), $e.RecordId,
            $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), ($prev.TimeCreated - $e.TimeCreated).TotalMinutes)
    }
    $prev = $e
}
''
'=== BSOD: System 1001 / BugCheck (все) ==='
foreach ($e in (@($all | Where-Object { $_.Id -eq 1001 -or $_.ProviderName -like '*BugCheck*' }) | Sort-Object RecordId)) {
    $x = [xml]$e.ToXml()
    $txt = @()
    foreach ($d in $x.Event.EventData.Data) { if ($d.'#text') { $txt += $d.'#text' } }
    ('  rec {0} {1}  {2}' -f $e.RecordId, $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), (($txt -join ' | ')))
}
''
'=== ошибки диска/файловой системы (disk/nvme/storahci/Ntfs/volmgr) ==='
$stor = @($all | Where-Object { $_.ProviderName -match 'disk|stornvme|storahci|Ntfs|volmgr|Chkdsk' -and $_.Level -le 3 } | Sort-Object RecordId | Select-Object -Last 25)
foreach ($e in $stor) {
    ('  rec {0} {1}  {2}/{3}' -f $e.RecordId, $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), $e.ProviderName, $e.Id)
}
if (-not $stor.Count) { '  нет' }
''
'=== minidump / memory.dmp (время файлов - UTC, #183/б.227: LastWriteTime тоже конвертится в зону PE) ==='
foreach ($d in @('W:\Windows\Minidump', 'W:\Windows\LiveKernelReports')) {
    if (Test-Path $d) {
        $f = @(Get-ChildItem $d -Recurse -Filter *.dmp -ErrorAction SilentlyContinue)
        ('  ' + $d + ': ' + $f.Count + ' шт')
        foreach ($x in ($f | Sort-Object LastWriteTimeUtc | Select-Object -Last 15)) {
            ('    {0}  {1} KB  {2} UTC' -f $x.Name, [int]($x.Length / 1KB), $x.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss'))
        }
    }
    else { ('  ' + $d + ': нет папки') }
}
if (Test-Path 'W:\Windows\MEMORY.DMP') {
    $m = Get-Item 'W:\Windows\MEMORY.DMP'
    ('  MEMORY.DMP: ' + [int]($m.Length / 1MB) + ' MB  ' + $m.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + ' UTC')
}
else { '  MEMORY.DMP: нет' }
