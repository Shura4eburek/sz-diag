<#
    Снять сырой ответ API учётной системы по одной заявке и положить в файл.

    Грабля, породившая рецепт: DTO под ответ нельзя строить по описанию API — нужен
    настоящий ответ, а собирать руками POST с токеном каждый раз муторно.

    Порт, путь к токену и имя заголовка — параметры без дефолтов: репозиторий публичный,
    конкретика живёт в локальной доке docs\erp-api.md (она вне git).

    Пример:
      .\erp-fetch-raw.ps1 -Sz 160800 -Port 1234 -TokenFile C:\path\to\token `
          -TokenHeader X-Some-Token -Out .\raw.json

    Должно вырасти в: szcli sz fetch <СЗ>
#>
param(
    [Parameter(Mandatory)] [string]$Sz,
    [Parameter(Mandatory)] [int]$Port,
    [Parameter(Mandatory)] [string]$TokenFile,
    [Parameter(Mandatory)] [string]$TokenHeader,
    [Parameter(Mandatory)] [string]$Out
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $TokenFile)) { throw "Файла токена нет: $TokenFile" }
$token = (Get-Content $TokenFile -Raw).Trim()
$base = "http://127.0.0.1:$Port"
$headers = @{ $TokenHeader = $token }

function Invoke-Tool([string]$Name, $Arguments) {
    $body = @{ name = $Name; arguments = $Arguments } | ConvertTo-Json -Depth 6
    return Invoke-RestMethod -Uri "$base/call" -Method Post -Headers $headers `
        -ContentType "application/json; charset=utf-8" -Body $body -TimeoutSec 180
}

Write-Host "== захват учётной программы: НЕ ТРОГАЙ МЫШЬ, ~1 минута ==" -ForegroundColor Yellow
Invoke-Tool "session.begin" @{} | Out-Null
try {
    $response = Invoke-Tool "sz.fetch" @{ number = $Sz }
    $response.result | ConvertTo-Json -Depth 12 | Set-Content -Path $Out -Encoding utf8
    Write-Host "Ответ сохранён: $Out" -ForegroundColor Green
}
finally {
    # Захват обязан отпускаться, иначе следующий вызов упрётся в busy, а у человека
    # останутся свёрнутые окна и чужой фильтр в панели поиска.
    Invoke-Tool "session.end" @{} | Out-Null
    Write-Host "Захват отпущен."
}
