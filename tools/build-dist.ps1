<#
.SYNOPSIS
  Собирает готовый к запуску dist: хост (hub + cli) и клиент (agent),
  генерит SSH-ключ сервиса и пишет конфиги. Запускать из корня репозитория
  или откуда угодно — путь вычисляется сам.

.PARAMETER HubIp
  Адрес хоста для конфига агента. По умолчанию localhost (хост и клиент —
  одна машина). Если клиент — отдельная машина/ВМ, укажи LAN-IP хоста,
  напр.:  .\tools\build-dist.ps1 -HubIp 192.168.94.239

.PARAMETER Port
  Порт hub (по умолчанию 5099).

.PARAMETER Token
  Общий токен hub/cli/agent (по умолчанию dev-token).

.EXAMPLE
  .\tools\build-dist.ps1
  .\tools\build-dist.ps1 -HubIp 192.168.1.50
#>
param(
    [string]$HubIp = "localhost",
    [int]$Port = 5099,
    [string]$Token = "dev-token"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "== sz-diag: сборка dist (HubIp=$HubIp, Port=$Port) ==" -ForegroundColor Cyan

# 0. Проверки окружения
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "Не найден dotnet SDK." }
if (-not (Get-Command ssh-keygen -ErrorAction SilentlyContinue)) { throw "Не найден ssh-keygen (OpenSSH client)." }

# 1. SSH-ключ сервиса (приватный остаётся на хосте, публичный уедет агенту)
New-Item -ItemType Directory secrets -Force | Out-Null
if (-not (Test-Path secrets\svc_diag_key)) {
    Write-Host "-- генерирую ключ сервиса secrets\svc_diag_key"
    ssh-keygen -t ed25519 -f secrets\svc_diag_key -N '""' -C szdiag-service -q
} else {
    Write-Host "-- ключ secrets\svc_diag_key уже есть, пропускаю"
}

# 2. Публикация self-contained single-file exe
Write-Host "-- публикую hub / cli / agent (это займёт минуту)"
Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
$common = @("-c","Release","-r","win-x64","--self-contained","-p:PublishSingleFile=true","-v","q","--nologo")
dotnet publish src/SzDiag.Hub   @common -o dist/host/hub  | Out-Null
dotnet publish src/SzDiag.Cli   @common -o dist/host/cli  | Out-Null
dotnet publish src/SzDiag.Agent @common -o dist/client    | Out-Null
Copy-Item secrets\svc_diag_key.pub dist\client\service_key.pub -Force

# 3. Конфиги (абсолютные пути для хоста — под ЭТУ машину; относительные для агента)
$kb = ("$root\dist\host\kb").Replace('\','\\')
$db = ("$root\dist\host\szdiag.db").Replace('\','\\')

@"
{
  "Urls": "http://0.0.0.0:$Port",
  "Hub": {
    "AgentToken": "$Token",
    "ManagementToken": "$Token",
    "SqliteConnectionString": "Data Source=$db",
    "KnowledgeBaseRoot": "$kb",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15"
  }
}
"@ | Set-Content dist\host\hub\appsettings.json -Encoding utf8

@"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb"
}
"@ | Set-Content dist\host\cli\appsettings.json -Encoding utf8

@"
{
  "HubUrl": "http://$($HubIp):$($Port)",
  "AgentToken": "$Token",
  "ServiceAccount": "svc-diag",
  "ServicePublicKeyPath": "service_key.pub",
  "SshPort": 22,
  "WatchdogHours": 1,
  "HeartbeatSeconds": 15,
  "StatePath": "C:\\ProgramData\\szdiag\\state.json",
  "TestSuitePath": "testsuite.json"
}
"@ | Set-Content dist\client\appsettings.json -Encoding utf8

# 4. Удобные лаунчеры на хосте
Set-Content dist\host\start-hub.cmd "@echo off`r`ncd /d `"%~dp0hub`"`r`nSzDiag.Hub.exe`r`npause" -Encoding ascii
Set-Content dist\host\szcli.cmd "@echo off`r`n`"%~dp0cli\SzDiag.Cli.exe`" %*" -Encoding ascii

Write-Host ""
Write-Host "== Готово ==" -ForegroundColor Green
Write-Host "Хост:    dist\host\   (start-hub.cmd, szcli.cmd)"
Write-Host "Клиент:  dist\client\ (SzDiag.Agent.exe + ключ + testsuite)"
Write-Host ""
Write-Host "Дальше: docs\TESTING.md. Не забудь открыть порт $Port на хосте:" -ForegroundColor Yellow
Write-Host "  New-NetFirewallRule -DisplayName 'szdiag-hub-$Port' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow"
