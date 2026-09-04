$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Разведка формата дампа tracerpt: какие события DXGI и с какой частотой шлёт игра (СЗ 123456).
#
# Грабля: pid в CSV от tracerpt лежит в колонке 10 в виде 0x000057F0 — с ведущими нулями и
# заглавными буквами. Фильтр вида ('0x{0:x}' -f $pid) даёт 0 совпадений и создаёт ложное
# впечатление, что событий по процессу нет. Сравнивать надо числом: [Convert]::ToInt32(поле,16).
#
#   szcli exec <СЗ> -f tools\recipes\client\etw-present-shape.ps1

$ErrorActionPreference = 'SilentlyContinue'
$name = 'szdiag-shape'
$etl  = Join-Path $env:TEMP "$name.etl"
$csv  = Join-Path $env:TEMP "$name.csv"
$sec  = 5
$proc = Get-Process Stalker2-Win64-Shipping
'   pid игры: {0}   замер {1} с' -f $proc.Id, $sec

logman stop $name -ets 2>&1 | Out-Null
Remove-Item $etl, $csv -Force
logman start $name -p '{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}' 0xffffffffffffffff 4 -ets -o $etl -nb 16 16 -bs 1024 2>&1 | Out-Null
Start-Sleep -Seconds $sec
logman stop $name -ets 2>&1 | Out-Null
tracerpt $etl -o $csv -of CSV -y 2>&1 | Out-Null

$rows = Get-Content $csv | Select-Object -Skip 1
$mine = foreach ($r in $rows) {
    $f = $r -split ','
    if ($f.Count -lt 11) { continue }
    $p = 0
    [void][int]::TryParse(($f[9].Trim() -replace '^0x',''), [Globalization.NumberStyles]::HexNumber, $null, [ref]$p)
    if ($p -eq $proc.Id) { [pscustomobject]@{ Id = $f[2].Trim(); Type = $f[1].Trim(); Task = $f[7].Trim() } }
}
'   событий процесса за {0} с: {1:N0}' -f $sec, $mine.Count
'=== Event ID / тип → событий в секунду ==='
$mine | Group-Object Id, Type | Sort-Object Count -Descending | Select-Object -First 12 |
    ForEach-Object { '   ID {0,-22} {1,8:N0} шт   {2,7:N1}/с' -f $_.Name, $_.Count, ($_.Count / $sec) }

Remove-Item $etl, $csv -Force
