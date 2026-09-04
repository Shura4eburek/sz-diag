$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Не «включается ли» FG, а «виден ли эффект»: частота монитора, V-Sync/лимиты в профиле драйвера,
# какая копия игры реально запускалась (СЗ 123456).
#
# Грабля: HAGS оказался включён (dxdiag: Hardware Scheduling Enabled:True), конфиг игры хранит
# Method=DLSSG/Mode=On2x — то есть игра настройку приняла. Значит следующий подозреваемый не
# «FG выключен», а потолок кадров: V-Sync/Max Frame Rate в панели NVIDIA или частота монитора,
# в которую упирается уже удвоенный кадр. Плюс на машине ДВЕ копии игры (Steam на E: и Game Pass
# в WindowsApps) — важно, какая запускается.
#
#   szcli exec <СЗ> -f tools\recipes\client\framegen-display-limits.ps1

$ErrorActionPreference = 'SilentlyContinue'

'=== Мониторы: модель, режим, частота ==='
Get-CimInstance -Namespace root\wmi WmiMonitorID | ForEach-Object {
    $n = ($_.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ }) -join ''
    $m = ($_.ManufacturerName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ }) -join ''
    '   {0} {1}   год {2}' -f $m, $n, $_.YearOfManufacture
}
Get-CimInstance Win32_VideoController | ForEach-Object {
    '   режим рабочего стола: {0}x{1} @ {2} Гц' -f $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate
}
'   -- все поддерживаемые режимы текущего монитора (топ по частоте)'
Get-CimInstance -Namespace root\wmi WmiMonitorListedSupportedSourceModes | ForEach-Object {
    $_.MonitorSourceModes | Sort-Object VerticalActivePixels, { $_.VerticalRefreshRateNumerator } -Descending |
        Select-Object -First 5 |
        ForEach-Object { '      {0}x{1} @ {2:N0} Гц' -f $_.HorizontalActivePixels, $_.VerticalActivePixels, ($_.VerticalRefreshRateNumerator / [Math]::Max(1,$_.VerticalRefreshRateDenominator)) }
}

'=== Профиль драйвера: строки про Stalker в базе DRS ==='
$drs = Join-Path $env:ProgramData 'NVIDIA Corporation\Drs\nvdrsdb0.bin'
if (Test-Path $drs) {
    $bytes = [IO.File]::ReadAllBytes($drs)
    $txt = [Text.Encoding]::Unicode.GetString($bytes)
    $hits = [regex]::Matches($txt, '[\x20-\x7E]{4,}') | ForEach-Object { $_.Value } |
        Where-Object { $_ -match 'Stalker|S\.T\.A\.L|Chornobyl' } | Sort-Object -Unique
    if ($hits) { $hits | ForEach-Object { '   ' + $_ } } else { '   персонального профиля для игры в базе не найдено (используется глобальный)' }
    '   nvdrsdb0.bin изменён {0:dd.MM.yyyy HH:mm}' -f (Get-Item $drs).LastWriteTime
}

'=== Какая копия игры запускалась (журнал Application/ProcessCreate + время exe) ==='
foreach ($exe in @(
    'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl\Stalker2\Binaries\Win64\Stalker2-Win64-Shipping.exe',
    'D:\XBOX\S.T.A.L.K.E.R. 2\Content\gamelaunchhelper.exe')) {
    if (Test-Path $exe) {
        $i = Get-Item $exe
        '   {0}' -f $exe
        '      изменён {0:dd.MM.yyyy HH:mm}   последний доступ {1:dd.MM.yyyy HH:mm}' -f $i.LastWriteTime, $i.LastAccessTime
    }
}
'   -- Prefetch по игре'
Get-ChildItem 'C:\Windows\Prefetch\*STALKER*','C:\Windows\Prefetch\*Stalker2*' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 8 |
    ForEach-Object { '      {0,-46} запуск {1:dd.MM.yyyy HH:mm}' -f $_.Name, $_.LastWriteTime }

'=== Оверлеи NVIDIA App / статистика производительности ==='
Get-ChildItem "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App" -Directory | ForEach-Object { '   ' + $_.Name }
'   NVIDIA App установлена: {0}' -f (Test-Path 'C:\Program Files\NVIDIA Corporation\NVIDIA App')
