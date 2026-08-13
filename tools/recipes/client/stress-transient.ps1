$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ТРАНЗИЕНТНЫЙ прогон: качели «нагрузка ↔ простой» вместо ровного стресса.
#
# Породила СЗ 161716. Там 75 минут ровного y-cruncher на профиле 6000 прошли ЧИСТО, при том
# что машина у клиента вырубалась пачками. Разбор игровых сессий показал почему: все 5 hard-off
# случились НЕ под нагрузкой — первый через 6 минут после штатного выхода из CS2, остальные по
# простаивающей машине. То есть убивает не нагрузка, а ПЕРЕХОД между режимами: сброс частот,
# смена C-state, скачок тока на VRM/БЖ. Ровный тест такие переходы не создаёт вообще — он один
# раз поднял нагрузку и держит, поэтому и «проходит» на дефектной машине.
# Именно поэтому у клиента ловил OCCT Power (он циклит), а TM5 и ровный стресс — нет.
#
# Цикл: CPU+RAM (y-cruncher) и GPU (FurMark) поднимаются вместе, держатся $OnSec,
# потом обрываются и машина стоит $OffSec. И так до $TotalMin.
# Резкий обрыв нагрузки здесь — не грубость, а суть теста: именно на нём машина и падает.
#   szcli exec <СЗ> -f tools\recipes\client\stress-transient.ps1 --detach
$Sz       = '000000'   # ← номер СЗ
$OnSec    = 60         # ← сколько держим нагрузку
$OffSec   = 40         # ← сколько стоим в простое
$TotalMin = 90         # ← общая длительность прогона
$MemG     = 8          # ← сколько ГБ отдаём y-cruncher
$WithGpu  = $true      # ← гнать ли FurMark в паре (транзиенты по линии 12V сильнее)

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent
function Find-Tool([string]$rel) {
    @("$base\tools\$rel", "C:\ProgramData\szdiag\tools\$rel") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
$yc = Find-Tool 'ycruncher\y-cruncher.exe'
$fm = Find-Tool 'furmark\furmark.exe'
if (-not $yc) { throw "y-cruncher не найден — szcli push $Sz ycruncher" }
if ($WithGpu -and -not $fm) { 'FurMark не найден — иду без GPU'; $WithGpu = $false }

if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null }
$log = 'C:\OCCT\transient.log'
"старт: {0:dd.MM HH:mm:ss}  цикл {1}с нагрузка / {2}с простой, всего {3} мин" -f (Get-Date), $OnSec, $OffSec, $TotalMin |
    Tee-Object -FilePath $log -Append

$deadline = (Get-Date).AddMinutes($TotalMin)
$n = 0
while ((Get-Date) -lt $deadline) {
    $n++
    # y-cruncher: -TL чуть больше окна, всё равно снимаем принудительно — обрыв и есть цель
    $ycP = Start-Process -FilePath $yc -WorkingDirectory (Split-Path $yc -Parent) `
        -ArgumentList 'stress', "-M:${MemG}G", "-D:$OnSec", "-TL:$($OnSec + 30)", 'VT3', 'N63' `
        -WindowStyle Hidden -PassThru
    $fmP = $null
    if ($WithGpu) {
        $fmP = Start-Process -FilePath $fm -WorkingDirectory (Split-Path $fm -Parent) `
            -ArgumentList '--demo', 'furmark-gl', '--width', '1280', '--height', '720', '--max-time', $OnSec `
            -WindowStyle Hidden -PassThru
    }

    Start-Sleep -Seconds $OnSec

    foreach ($p in @($ycP, $fmP)) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    # дочерний бинарь y-cruncher живёт отдельно от лаунчера — добиваем по пути
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*\ycruncher\Binaries\*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    # Метка ПОСЛЕ снятия нагрузки: если машина падает здесь, последняя строка лога покажет,
    # на каком именно цикле и в какой фазе (161716 — вырубоны шли по простаивающей машине).
    "цикл {0}: {1:HH:mm:ss} нагрузка снята, простой {2}с" -f $n, (Get-Date), $OffSec |
        Tee-Object -FilePath $log -Append
    Start-Sleep -Seconds $OffSec
}
"финиш: {0:dd.MM HH:mm:ss}, циклов {1}" -f (Get-Date), $n | Tee-Object -FilePath $log -Append
