<#
.SYNOPSIS
  Собирает готовый к запуску dist: хост (hub + cli) и клиент (agent),
  генерит SSH-ключ сервиса и пишет конфиги.

.PARAMETER HubIp
  Адрес хоста для конфига агента. По умолчанию пусто — агент сам найдёт hub через
  автообнаружение в локальной сети (UDP-broadcast, см. HubDiscovery). Если клиент в
  другой сети/VPN без broadcast, или нужен жёсткий адрес — укажи явно:
    .\tools\build-dist.ps1 -HubIp 192.168.94.239
    .\tools\build-dist.ps1 -HubIp localhost

.PARAMETER Port
  Порт hub (по умолчанию 5099).

.PARAMETER Token
  Общий токен hub/cli/agent (по умолчанию dev-token).
#>
param(
    [string]$HubIp = "",
    [int]$Port = 5099,
    [string]$Token = "dev-token"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$hubIpLabel = if ([string]::IsNullOrWhiteSpace($HubIp)) { "авто (UDP-обнаружение)" } else { $HubIp }
Write-Host "== sz-diag: сборка dist (HubIp=$hubIpLabel Port=$Port) =="

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "Не найден dotnet SDK." }
if (-not (Get-Command ssh-keygen -ErrorAction SilentlyContinue)) { throw "Не найден ssh-keygen (OpenSSH client)." }

# 1. SSH-ключ сервиса (приватный остаётся на хосте, публичный уедет агенту)
New-Item -ItemType Directory secrets -Force | Out-Null
if (-not (Test-Path secrets\svc_diag_key)) {
    Write-Host "-- генерирую ключ secrets\svc_diag_key"
    # Через cmd: PowerShell ломает пустой пароль (-N '""' даёт литеральные кавычки,
    # ключ выходит зашифрованным). В cmd `-N ""` — честный пустой пароль.
    cmd /c 'ssh-keygen -t ed25519 -f secrets\svc_diag_key -C szdiag-service -N "" -q'
} else {
    Write-Host "-- ключ secrets\svc_diag_key уже есть"
}

# 2. Публикация self-contained single-file exe.
# Чистим только сами билды (hub/cli/agent) — dist\host\kb и szdiag.db это runtime-данные
# живого хаба (история СЗ, база знаний), а не билд-артефакт: пересборка их не должна сносить.
Write-Host "-- публикую hub / cli / agent (минуту)"
Remove-Item dist\host\hub, dist\host\cli, dist\client -Recurse -Force -ErrorAction SilentlyContinue
$common = "-c","Release","-r","win-x64","--self-contained","-p:PublishSingleFile=true","-v","q","--nologo"

# dotnet.exe — внешний процесс: $ErrorActionPreference="Stop" на его код возврата не
# действует. Без явной проверки $LASTEXITCODE неудачная публикация (например, exe
# залочен уже запущенным процессом) молча проходит мимо — вывод у dotnet publish
# заглушен через Out-Null, и скрипт бодро репортует "Готово", хотя файл не обновился.
function Publish($project, $out) {
    dotnet publish $project @common -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish $project упал (код $LASTEXITCODE). Частая причина — exe уже " +
              "запущен (szcli/hub/агент открыты в другом окне) и файл залочен. Закрой их и повтори."
    }
}
Publish "src/SzDiag.Hub" "dist/host/hub"
Publish "src/SzDiag.Cli" "dist/host/cli"
Publish "src/SzDiag.Agent" "dist/client"
Copy-Item secrets\svc_diag_key.pub dist\client\service_key.pub -Force

# 2b. Портативные стресс-утилиты (TM5 / OCCT / 3DMark / FurMark и пр.).
# Кладутся в client-tools\<name>\ (в .gitignore — бинарники и лицензии не коммитим),
# при сборке уезжают в dist\client\tools\. Пути в testsuite.json — tools\<name>\...
if (Test-Path client-tools) {
    Write-Host "-- копирую client-tools -> dist\client\tools"
    New-Item -ItemType Directory dist\client\tools -Force | Out-Null
    Copy-Item client-tools\* dist\client\tools\ -Recurse -Force
} else {
    Write-Host "-- client-tools нет: стресс-утилиты не вложены (шаги app сообщат 'не найден exe')"
}

# 3. Конфиги (абсолютные пути хоста — под ЭТУ машину; относительные — агенту)
$kb = ("$root\dist\host\kb").Replace('\', '\\')
$db = ("$root\dist\host\szdiag.db").Replace('\', '\\')

$hubCfg = @"
{
  "Urls": "http://0.0.0.0:$Port",
  "Hub": {
    "AgentToken": "$Token",
    "ManagementToken": "$Token",
    "Port": $Port,
    "SqliteConnectionString": "Data Source=$db",
    "KnowledgeBaseRoot": "$kb",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15"
  }
}
"@
Set-Content -Path dist\host\hub\appsettings.json -Value $hubCfg -Encoding utf8

$cliCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb"
}
"@
Set-Content -Path dist\host\cli\appsettings.json -Value $cliCfg -Encoding utf8

$hubUrlValue = if ([string]::IsNullOrWhiteSpace($HubIp)) { "" } else { "http://$($HubIp):$($Port)" }

$agentCfg = @"
{
  "HubUrl": "$hubUrlValue",
  "AgentToken": "$Token",
  "ServiceAccount": "svc-diag",
  "ServicePublicKeyPath": "service_key.pub",
  "SshPort": 22,
  "WatchdogHours": 1,
  "HeartbeatSeconds": 15,
  "StatePath": "C:\\ProgramData\\szdiag\\state.json",
  "TestSuitePath": "testsuite.json"
}
"@
Set-Content -Path dist\client\appsettings.json -Value $agentCfg -Encoding utf8

# 4. Удобные лаунчеры на хосте (single-quoted here-string — литералы)
$startHub = @'
@echo off
cd /d "%~dp0hub"
SzDiag.Hub.exe
pause
'@
Set-Content -Path dist\host\start-hub.cmd -Value $startHub -Encoding ascii

$szcli = @'
@echo off
"%~dp0cli\SzDiag.Cli.exe" %*
'@
Set-Content -Path dist\host\szcli.cmd -Value $szcli -Encoding ascii

Write-Host ""
Write-Host "== Готово =="
Write-Host "Хост:   dist\host\   (start-hub.cmd, szcli.cmd)"
Write-Host "Клиент: dist\client\ (SzDiag.Agent.exe + ключ + testsuite)"
Write-Host "Гайд:   docs\TESTING.md"
Write-Host "Открой порт на хосте (от админа):"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-hub-$Port -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-discovery-5098 -Direction Inbound -Protocol UDP -LocalPort 5098 -Action Allow"
