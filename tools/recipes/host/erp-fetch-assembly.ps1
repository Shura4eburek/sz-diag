<#
.SYNOPSIS
  Тянет вкладку «Збірка» (комплектующие с серийниками) из учётной системы по номеру сборки.

.DESCRIPTION
  Грабля, породившая рецепт (161642, 07.09): `szcli sz fetch` состав НЕ принёс — на стороне
  TeleAuto классификатор `config_source` считает заказ «готовым решением» только при РОВНО
  одной товарной строке, а в заказе кроме компа была вторая строка-услуга («Пакет Швидкий
  старт +»). Итог: `assembly: null`, состав ПК с серийниками потерян, хотя во вкладке
  «Збірка» он есть.

  Пока `sz fetch` не научился игнорировать строки-услуги, состав добывается этим маршрутом
  напрямую по API: фильтр по номеру сборки → открыть документ → прочитать грид.

  Номер сборки = хвост серийника заявки: «1891675-44806» → «44806».

.PARAMETER Sz
  Номер СЗ (6 цифр) — только для имени файла результата в kb.

.PARAMETER Key
  Номер сборки. Если не задан — берётся из kb\СЗ\<Sz>\erp.json (поле SN).

.EXAMPLE
  .\tools\recipes\host\erp-fetch-assembly.ps1 -Sz 161642
  .\tools\recipes\host\erp-fetch-assembly.ps1 -Sz 161642 -Key 44806
#>
param(
    [Parameter(Mandatory = $true)][string]$Sz,
    [string]$Key = "",
    [string]$BaseUrl = "http://127.0.0.1:8765",
    [string]$TokenFile = "",
    [string]$KbRoot = "C:\Users\ENDI\RiderProjects\sz-diag\dist\host\kb"
)

$ErrorActionPreference = "Stop"

# Токен пишется рядом с ЗАПУЩЕННЫМ TeleAuto.exe, а не в репозитории: копии в
# PycharmProjects\TeleAuto (корень и dist\) протухшие и дают 401.
if (-not $TokenFile) {
    $proc = Get-CimInstance Win32_Process -Filter "Name = 'TeleAuto.exe'" |
            Select-Object -First 1 -ExpandProperty ExecutablePath
    if (-not $proc) { throw "TeleAuto.exe не запущен — некуда стучаться." }
    $TokenFile = Join-Path (Split-Path $proc -Parent) "telemart_api_token"
}
if (-not (Test-Path $TokenFile)) { throw "нет файла токена: $TokenFile" }
$token = (Get-Content $TokenFile -Raw).Trim()

$szDir = Join-Path $KbRoot "СЗ\$Sz"

if (-not $Key) {
    $erpJson = Join-Path $szDir "erp.json"
    if (-not (Test-Path $erpJson)) { throw "нет $erpJson — сначала прогони szcli sz fetch $Sz" }
    $erp = Get-Content $erpJson -Raw -Encoding UTF8 | ConvertFrom-Json
    $serial = $erp.request.fields.SN
    if (-not $serial) { throw "в erp.json нет поля SN — номер сборки взять неоткуда." }
    $Key = ($serial -split '-')[-1]
    Write-Host "-- номер сборки из SN '$serial': $Key"
}

function Call($name, $arguments) {
    $body = @{ name = $name; arguments = $arguments } | ConvertTo-Json -Depth 6 -Compress
    # Тело — БАЙТАМИ в UTF-8: PowerShell 5.1 отправляет строку в latin1, и кириллица в
    # значениях уезжает в «?» (сервер отвечал «фильтра «??????» нет на панели»).
    # Сервер (BaseHTTPRequestHandler) читает РОВНО Content-Length: тело шлём буфером,
    # никакого chunked, иначе приходит «нет поля name» на валидном JSON.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    return Invoke-RestMethod -Method Post -Uri "$BaseUrl/call" -Body $bytes `
        -ContentType "application/json; charset=utf-8" `
        -Headers @{ "X-TeleAuto-Token" = $token } -TimeoutSec 300
}

$view = "AssemblyServicesView"
Write-Host "-- обращение к учётной системе (кликает по интерфейсу — не трогай мышь)"
Call "session.begin" @{} | Out-Null
try {
    Call "tab.ensure"    @{ view = $view } | Out-Null
    Call "filters.set"   @{ view = $view; name = "Збірка"; value = $Key } | Out-Null
    # Статус сборки заранее неизвестен (собрана/выдана/в работе) — фильтр снимаем,
    # иначе дефолтный набор статусов прячет нужную строку.
    try { Call "filters.clear" @{ view = $view; name = "Статус" } | Out-Null }
    catch { Write-Host "   фильтр «Статус» не снялся (возможно уже пуст): $($_.Exception.Message)" }
    $found = Call "filters.apply" @{ view = $view }
    Write-Host "-- найдено строк: $($found.result.rows)"

    $opened = Call "result.open" @{ view = $view; expect = "Збірка" }
    $document = $opened.result.document
    Write-Host "-- открыт документ: $document"

    $grids = (Call "document.grids" @{ document = $document }).result.grids
    Write-Host "-- гриды: $($grids -join ', ')"

    $rows = (Call "document.grid" @{ document = $document; grid = "GridControl" }).result.rows
    $tabs = (Call "document.tabs"  @{ document = $document }).result.rows

    $payload = [ordered]@{
        key        = $Key
        document   = $document
        grids      = $grids
        tabs       = $tabs
        components = $rows
    }

    if (-not (Test-Path $szDir)) { New-Item -ItemType Directory $szDir -Force | Out-Null }
    $out = Join-Path $szDir "assembly.json"
    $payload | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding UTF8
    Write-Host "-- сохранено: $out"

    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

    try { Call "document.close" @{ document = $document } | Out-Null } catch { }
}
finally {
    # Сессия держит окна и фильтры человека — закрыть обязательно, иначе следующий
    # вызов упрётся в «захват занят».
    try { Call "session.end" @{} | Out-Null } catch { }
}
