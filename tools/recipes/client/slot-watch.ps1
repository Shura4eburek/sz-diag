$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# ВАХТА ЗА НАКОПИТЕЛЕМ В СЛОТЕ: ловит момент, когда диск пропадает с шины.
#
# Грабля (СЗ 161346, 17.08): дефект — отвал накопителя с шины в чипсетном слоте. Пока диск
# системный, отвал виден как падение машины (0xEF) и её проще заметить. Но в контрольном тесте
# в слот ставится НЕсистемный диск: при его отвале система выживет и просто продолжит работать —
# событие пройдёт незамеченным, если за ним никто не следит. Плюс `stornvme 129` (reset) может
# случиться и без исчезновения диска: это ранняя стадия того же дефекта, и она тоже нужна.
#
# Пишет строку раз в 5 секунд в лог с флешем — переживает вырубон машины.
# Запускать detached (scheduled task под SYSTEM), иначе умрёт вместе с ssh-сессией.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\slot-watch.ps1 --detach

$Minutes  = 300
$Log      = 'C:\ProgramData\szdiag\slot-watch.log'
$EverySec = 5

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log = $Log -replace '\.log$', "-$stamp.log"
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true

function Say([string]$m) {
    $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m
    $line
    $sw.WriteLine($line)
}

# Опорный состав дисков: всё, что отличается от него, — событие
$baseline = @(Get-PhysicalDisk -ErrorAction SilentlyContinue |
    Where-Object BusType -eq 'NVMe' | Sort-Object FriendlyName |
    ForEach-Object { $_.FriendlyName })
Say "СТАРТ вахты на $Minutes мин. Опорный состав NVMe: $($baseline -join ', ')"

$deadline = (Get-Date).AddMinutes($Minutes)
$lastEventCheck = Get-Date
$prev = $baseline -join '|'
$flaps = 0

while ((Get-Date) -lt $deadline) {
    $now = @(Get-PhysicalDisk -ErrorAction SilentlyContinue |
        Where-Object BusType -eq 'NVMe' | Sort-Object FriendlyName |
        ForEach-Object { $_.FriendlyName })
    $cur = $now -join '|'
    if ($cur -ne $prev) {
        $flaps++
        $gone = $baseline | Where-Object { $_ -notin $now }
        $back = $now | Where-Object { $_ -notin ($prev -split '\|') }
        if ($gone) { Say "!!! ПРОПАЛ С ШИНЫ: $($gone -join ', ')  (состав сейчас: $cur)" }
        if ($back) { Say ">>> ВЕРНУЛСЯ: $($back -join ', ')" }
        $prev = $cur
    }
    # События контроллера идут и без исчезновения диска — это ранняя стадия того же отказа
    if (((Get-Date) - $lastEventCheck).TotalSeconds -ge 30) {
        $ev = @(Get-WinEvent -FilterHashtable @{
                LogName      = 'System'
                ProviderName = @('disk', 'stornvme', 'storahci', 'Ntfs', 'volmgr', 'Microsoft-Windows-WHEA-Logger')
                StartTime    = $lastEventCheck
            } -ErrorAction SilentlyContinue |
            Where-Object { $_.LevelDisplayName -notin 'Сведения', 'Information', 'Відомості' })
        foreach ($e in $ev) {
            $m = ($e.Message -replace '\s+', ' ').Trim()
            if ($m.Length -gt 110) { $m = $m.Substring(0, 110) }
            Say ">>> ЖУРНАЛ: $($e.ProviderName) Id=$($e.Id) — $m"
        }
        $lastEventCheck = Get-Date
    }
    Start-Sleep -Seconds $EverySec
}
Say "ФИНИШ. Пропаданий с шины: $flaps"
$sw.Dispose()
