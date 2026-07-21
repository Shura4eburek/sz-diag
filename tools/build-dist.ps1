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

# 0. Портативный Win32-OpenSSH для клиента (sshd.exe и ко). Качаем один раз с GitHub,
# кэшируем распакованным в client-tools\ssh — в git не коммитим (как OCCT/TM5).
$sshCache = "client-tools\ssh"
if (-not (Test-Path "$sshCache\sshd.exe")) {
    Write-Host "-- качаю портативный OpenSSH (один раз, ~10 МБ)"
    $rel = "https://github.com/PowerShell/Win32-OpenSSH/releases/download/v9.5.0.0p1-Beta/OpenSSH-Win64.zip"
    $zip = "$env:TEMP\OpenSSH-Win64.zip"
    New-Item -ItemType Directory $sshCache -Force | Out-Null
    try {
        Invoke-WebRequest -Uri $rel -OutFile $zip -UseBasicParsing
        Expand-Archive $zip "$env:TEMP\OpenSSH-Win64" -Force
        Copy-Item "$env:TEMP\OpenSSH-Win64\OpenSSH-Win64\*" $sshCache -Recurse -Force
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    } catch {
        throw "Не удалось скачать портативный OpenSSH ($rel): $($_.Exception.Message). " +
              "Проверь интернет на хосте или положи распакованные бинарники в $sshCache вручную."
    }
} else {
    Write-Host "-- портативный OpenSSH уже в кэше ($sshCache)"
}

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
$common = "-c","Release","-r","win-x64","--self-contained","-p:PublishSingleFile=true","-v","q","--nologo"

# Публикуем во временную папку рядом и меняем местами со старой ТОЛЬКО после успеха —
# иначе неудачная публикация (напр. exe залочен уже запущенным процессом: szcli/hub/агент
# открыты в другом окне) может снести старую рабочую версию раньше, чем станет ясно, что
# новая не соберётся. dotnet.exe — внешний процесс, $ErrorActionPreference="Stop" на его
# код возврата не действует, и вывод заглушен через Out-Null — без явной проверки
# $LASTEXITCODE такая неудача проходит молча, а скрипт бодро репортует "Готово".
function Publish($project, $out) {
    $staging = "$out.new"
    $backup = "$out.old"
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish $project @common -o $staging | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw "dotnet publish $project упал (код $LASTEXITCODE). Старая версия в $out не тронута."
    }

    # Переименование папки — одна атомарная операция метаданных, не трогает хендлы уже
    # открытых файлов внутри. Поштучное Remove-Item -Recurse на залоченной папке удаляет
    # файлы один за другим и падает на первом залоченном — оставляя папку НАПОЛОВИНУ
    # снесённой (ровно так один раз и потерялся appsettings.json). Rename либо срабатывает
    # целиком, либо не срабатывает вообще — старая версия остаётся невредимой в любом случае.
    if (Test-Path $out) {
        Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
        try { Rename-Item $out (Split-Path $backup -Leaf) -ErrorAction Stop }
        catch {
            throw "Собрано в $staging, но $out залочен (запущен hub/cli/агент из этой папки?) " +
                  "и не переименовался. Закрой процесс и запусти сборку ещё раз. Старая версия " +
                  "в $out цела и не тронута."
        }
    }
    Move-Item $staging $out
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
}
# Три компонента независимы (напр. hub может быть живым сервером и залоченным, пока мы
# правим только agent/cli) — сбой одного не должен мешать пересобрать остальные. Копим
# ошибки и валимся с сводкой только в самом конце, после того как попробовали все три.
$failed = @()
foreach ($p in @(
    @{ Project = "src/SzDiag.Hub"; Out = "dist/host/hub" },
    @{ Project = "src/SzDiag.Cli"; Out = "dist/host/cli" },
    @{ Project = "src/SzDiag.Agent"; Out = "dist/client" }
)) {
    try { Publish $p.Project $p.Out }
    catch {
        Write-Host "-- ПРОПУСК $($p.Project): $($_.Exception.Message)" -ForegroundColor Yellow
        $failed += $p.Project
    }
}
if (Test-Path dist\client\SzDiag.Agent.exe) {
    Copy-Item secrets\svc_diag_key.pub dist\client\service_key.pub -Force
}

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

# Портативный sshd рядом с агентом: dist\client\ssh
if (Test-Path dist\client\SzDiag.Agent.exe) {
    Write-Host "-- копирую OpenSSH -> dist\client\ssh"
    New-Item -ItemType Directory dist\client\ssh -Force | Out-Null
    Copy-Item "$sshCache\sshd.exe","$sshCache\ssh-keygen.exe","$sshCache\sftp-server.exe" dist\client\ssh\ -Force
    # dll-зависимости (libcrypto и пр.) лежат рядом с exe в релизе — берём все dll.
    Copy-Item "$sshCache\*.dll" dist\client\ssh\ -Force -ErrorAction SilentlyContinue
}

# Апдейтер кладём рядом с агентом в dist\client (та же папка, БЕЗ сноса — агент уже там,
# а функция Publish меняет папку целиком через staging и снесла бы его). Прямой publish -o.
if (Test-Path dist\client\SzDiag.Agent.exe) {
    Write-Host "-- публикую SzDiag.Updater -> dist\client"
    dotnet publish src/SzDiag.Updater @common -o dist\client | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish SzDiag.Updater упал (код $LASTEXITCODE)." }
}

# --- Версия и пакет для апдейтера ---
# Версия = git short sha (+ -dirty, если есть незакоммиченные правки); вне git — timestamp.
if (Test-Path dist\client\SzDiag.Agent.exe) {
    $version = ""
    try {
        $sha = (git -C $root rev-parse --short HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and $sha) {
            $dirty = (git -C $root status --porcelain 2>$null)
            $version = if ($dirty) { "$sha-dirty" } else { "$sha" }
        }
    } catch { }
    if ([string]::IsNullOrWhiteSpace($version)) { $version = Get-Date -Format "yyyyMMdd-HHmmss" }

    Set-Content -Path dist\client\version.txt -Value $version -Encoding ascii -NoNewline

    # Пакет: agent.exe + ssh + service_key.pub + testsuite.json + version.txt.
    # БЕЗ appsettings.json (локальный конфиг клиента), tools\ (тяжёлые тулы) и Updater.exe.
    $pkgStage = Join-Path $env:TEMP "szdiag-pkg"
    Remove-Item $pkgStage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $pkgStage -Force | Out-Null
    Copy-Item dist\client\SzDiag.Agent.exe    $pkgStage\ -Force
    Copy-Item dist\client\service_key.pub     $pkgStage\ -Force -ErrorAction SilentlyContinue
    Copy-Item dist\client\testsuite.json      $pkgStage\ -Force -ErrorAction SilentlyContinue
    Copy-Item dist\client\version.txt         $pkgStage\ -Force
    if (Test-Path dist\client\ssh) { Copy-Item dist\client\ssh $pkgStage\ssh -Recurse -Force }

    $distRoot = "dist\host\hub\agent-dist"
    New-Item -ItemType Directory $distRoot -Force | Out-Null
    $zipPath = Join-Path $distRoot "package.zip"
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$pkgStage\*" -DestinationPath $zipPath -Force

    $sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    Set-Content -Path (Join-Path $distRoot "package.sha256") -Value $sha256 -Encoding ascii -NoNewline
    Set-Content -Path (Join-Path $distRoot "version.txt")    -Value $version -Encoding ascii -NoNewline
    Remove-Item $pkgStage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "-- пакет апдейтера: $zipPath (version=$version)"
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
    "AgentDistRoot": "$(("$root\dist\host\hub\agent-dist").Replace('\','\\'))",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15"
  }
}
"@
# Пишем конфиг только если папка компонента реально существует — если его publish выше
# провалился и старой копии тоже никогда не было (напр. самый первый запуск), писать некуда.
if (Test-Path dist\host\hub) { Set-Content -Path dist\host\hub\appsettings.json -Value $hubCfg -Encoding utf8 }

$cliCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb"
}
"@
if (Test-Path dist\host\cli) { Set-Content -Path dist\host\cli\appsettings.json -Value $cliCfg -Encoding utf8 }

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
if (Test-Path dist\client) { Set-Content -Path dist\client\appsettings.json -Value $agentCfg -Encoding utf8 }

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
if ($failed.Count -gt 0) {
    Write-Host "== Готово частично — не пересобрались: $($failed -join ', ') ==" -ForegroundColor Yellow
    Write-Host "Старые версии этих компонентов не тронуты и продолжают работать. Закрой их процессы и запусти сборку ещё раз."
} else {
    Write-Host "== Готово =="
}
Write-Host "Хост:   dist\host\   (start-hub.cmd, szcli.cmd)"
Write-Host "Клиент: dist\client\ (SzDiag.Agent.exe + ключ + testsuite)"
Write-Host "Гайд:   docs\TESTING.md"
Write-Host "Открой порт на хосте (от админа):"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-hub-$Port -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-discovery-5098 -Direction Inbound -Protocol UDP -LocalPort 5098 -Action Allow"

if ($failed.Count -gt 0) { exit 1 }
