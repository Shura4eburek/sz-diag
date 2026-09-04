$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Различить «игра рисует 90 и выводит 90» от «рисует 90, а на экран уходит 180» (СЗ 123456).
#
# Грабля: событие Present провайдера Microsoft-Windows-DXGI (Event ID 42/43) считает вызовы
# Present ПРИЛОЖЕНИЯ. При работающей генерации кадров лишний кадр подставляет свопчейн
# Streamline уровнем ниже, поэтому по DXGI разницы между «FG работает» и «FG выключен» не видно —
# в обоих случаях будет базовая частота отрисовки. Различает только уровень ядра: Microsoft-
# Windows-DxgKrnl шлёт событие на каждый реальный flip на дисплей. Отношение flip/present ≈ 2
# при живом FG и ≈ 1 при мёртвом.
#
#   szcli exec <СЗ> -f tools\recipes\client\flip-rate-dxgkrnl.ps1

$ErrorActionPreference = 'SilentlyContinue'
$sec  = 5
$proc = Get-Process Stalker2-Win64-Shipping
if (-not $proc) { '   игра не запущена'; return }
'   pid игры: {0}   замер {1} с' -f $proc.Id, $sec

function Measure-Provider($tag, $guid, $keyword) {
    $etl = Join-Path $env:TEMP "szdiag-$tag.etl"
    $csv = Join-Path $env:TEMP "szdiag-$tag.csv"
    logman stop "szdiag-$tag" -ets 2>&1 | Out-Null
    Remove-Item $etl, $csv -Force
    logman start "szdiag-$tag" -p $guid $keyword 4 -ets -o $etl -nb 16 64 -bs 1024 2>&1 | Out-Null
    Start-Sleep -Seconds $sec
    logman stop "szdiag-$tag" -ets 2>&1 | Out-Null
    tracerpt $etl -o $csv -of CSV -y 2>&1 | Out-Null
    $rows = Get-Content $csv | Select-Object -Skip 1
    Remove-Item $etl, $csv -Force
    $rows
}

'=== DXGI: презенты приложения (ID 42/43) ==='
$dxgi = Measure-Provider 'dxgi' '{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}' '0xffffffffffffffff'
$appPresent = 0
foreach ($r in $dxgi) {
    $f = $r -split ','
    if ($f.Count -lt 11) { continue }
    $p = 0
    [void][int]::TryParse(($f[9].Trim() -replace '^0x',''), [Globalization.NumberStyles]::HexNumber, $null, [ref]$p)
    if ($p -eq $proc.Id -and $f[2].Trim() -eq '42') { $appPresent++ }
}
'   Present приложения: {0:N1}/с' -f ($appPresent / $sec)

'=== DxgKrnl: что происходит на уровне ядра ==='
$krnl = Measure-Provider 'dxgk' '{802EC45A-1E99-4B83-9920-87C98277BA9D}' '0x1'
$byId = @{}
foreach ($r in $krnl) {
    $f = $r -split ','
    if ($f.Count -lt 11) { continue }
    $p = 0
    [void][int]::TryParse(($f[9].Trim() -replace '^0x',''), [Globalization.NumberStyles]::HexNumber, $null, [ref]$p)
    $key = '{0}|{1}|{2}' -f $f[2].Trim(), $f[1].Trim(), $(if ($p -eq $proc.Id) { 'игра' } else { 'прочее' })
    $byId[$key] = 1 + $byId[$key]
}
'   кандидаты в кадровые события — частота 20..400/с (fps и vsync живут здесь):'
$byId.GetEnumerator() | Where-Object { ($_.Value / $sec) -ge 20 -and ($_.Value / $sec) -le 400 } |
    Sort-Object Value -Descending | Select-Object -First 20 |
    ForEach-Object { '      {0,-34} {1,8:N1}/с' -f $_.Key, ($_.Value / $sec) }

'=== опора для сравнения ==='
Get-CimInstance Win32_VideoController | ForEach-Object { '   монитор: {0} Гц' -f $_.CurrentRefreshRate }
