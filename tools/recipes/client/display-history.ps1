$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# История дисплеев: какие мониторы подключались, к какому адаптеру и НА КАКОЙ ЧАСТОТЕ.
# Грабля: 162003 — «инпут-лаг в CS» на 240-Гц мониторе; надо доказать, гнал ли он 240 Гц
# и не висел ли на iGPU. Данные — из GraphicsDrivers\Configuration (переживает переезды).
#   szcli exec <СЗ> -f tools\recipes\client\display-history.ps1

'== мониторы, которые винда когда-либо видела (Enum\DISPLAY)'
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\DISPLAY' -ErrorAction SilentlyContinue | ForEach-Object {
    $model = $_.PSChildName
    Get-ChildItem $_.PSPath | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        "   {0}  {1}  ({2})" -f $model, $p.DeviceDesc -replace '^.*;', '', $p.FriendlyName
        "      instance: $($_.PSChildName)"
    }
}

'== конфигурации дисплеев (кто, к чему, сколько Гц)'
$cfg = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration'
Get-ChildItem $cfg -ErrorAction SilentlyContinue | ForEach-Object {
    $setName = $_.PSChildName
    Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        $num = $p.'Timing.Active.VSyncFreq.Numerator'
        $den = $p.'Timing.Active.VSyncFreq.Denominator'
        $hz = if ($num -and $den) { [math]::Round($num / $den, 2) } else { '?' }
        $w = $p.'Timing.Active.ActiveSize.cx'; $h = $p.'Timing.Active.ActiveSize.cy'
        if (-not $w) { $w = $p.PrimSurfSize.cx }
        "   [{0}]" -f ($setName -replace '(.{70}).*', '$1…')
        "      {0}x{1} @ {2} Гц   (запись {3})" -f $w, $h, $hz, $_.PSChildName
    }
}

'== связка монитор → адаптер (Connectivity)'
$conn = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Connectivity'
Get-ChildItem $conn -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    "   {0}" -f $_.PSChildName
    $p.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { "      {0} = {1}" -f $_.Name, $_.Value }
}

'== Process Lasso: остаток конфига (правила по процессам)'
$ini = 'C:\ProgramData\ProcessLasso\config\prolasso.ini'
if (Test-Path $ini) {
    $lines = Get-Content $ini -Encoding UTF8
    $i = ($lines | Select-String -Pattern '^\[Logging\]').LineNumber
    if ($i) { $lines | Select-Object -Skip $i | Where-Object { $_ -match '\S' } | ForEach-Object { "   $_" } }
}

'== Process Lasso: лог событий (кого душил ProBalance)'
$logdir = 'C:\ProgramData\ProcessLasso\logs'
if (Test-Path $logdir) {
    Get-ChildItem $logdir | ForEach-Object { "   файл: {0}  {1:N0} байт  {2:yyyy-MM-dd HH:mm}" -f $_.Name, $_.Length, $_.LastWriteTime }
    $lg = Get-ChildItem $logdir -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($lg) {
        '   --- последние 60 строк ---'
        Get-Content $lg.FullName -Tail 60 | ForEach-Object { "   $_" }
        '   --- статистика по типам событий ---'
        Get-Content $lg.FullName | ForEach-Object { ($_ -split "`t")[1] } | Group-Object | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object { "   {0,6}  {1}" -f $_.Count, $_.Name }
    }
} else { '   логов нет' }
