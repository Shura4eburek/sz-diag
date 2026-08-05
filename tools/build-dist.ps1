<#
.SYNOPSIS
  Собирает готовый к запуску dist: хост (hub + cli) и клиент (agent),
  генерит SSH-ключ сервиса и пишет конфиги.

.PARAMETER HubIp
  Адрес хоста для конфига агента. По умолчанию пусто — агент сам найдёт hub через
  автообнаружение в локальной сети (UDP-broadcast, см. HubDiscovery). Если клиент в
  другой сети/VPN без broadcast, или нужен жёсткий адрес — укажи явно:
    .\tools\build-dist.ps1 -HubIp <HUB_LAN_IP>
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
# Конфиг компонента живёт своей жизнью: в собранном виде он дефолтный, а рабочий содержит
# абсолютные пути к kb/szdiag.db. При подмене файлов поверх его не трогаем.
$configFile = "appsettings.json"

# Хендлы убитого процесса отпускаются с задержкой, а свежий 90-мегабайтный exe после
# публикации ещё какое-то время держит антивирус. Пересборка сразу после Stop-Process
# падала ровно на этом, а через полминуты проходила — поэтому ретрай, а не одна попытка.
function Invoke-WithRetry([scriptblock]$action, [int]$attempts = 8, [int]$delayMs = 750) {
    for ($i = 1; $i -le $attempts; $i++) {
        try { & $action; return $true }
        catch {
            if ($i -eq $attempts) { return $false }
            Start-Sleep -Milliseconds $delayMs
        }
    }
    return $false
}

# Фоллбэк, когда папку не переименовать: копируем файлы поверх (кроме конфига). Помогает,
# когда залочен один-два файла из папки, а не она целиком. Возвращает список файлов,
# которые скопировать не удалось, — по нему решаем, обновился компонент или нет.
function Copy-Over($from, $to) {
    $failures = @()
    foreach ($src in Get-ChildItem $from -Recurse -File) {
        $rel = $src.FullName.Substring((Resolve-Path $from).Path.Length).TrimStart('\')
        if ($rel -ieq $configFile) { continue }
        $dst = Join-Path $to $rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory $dstDir -Force | Out-Null }
        if (-not (Invoke-WithRetry { Copy-Item $src.FullName $dst -Force -ErrorAction Stop } 4 500)) {
            $failures += $rel
        }
    }
    return $failures
}

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
        $renamed = Invoke-WithRetry { Rename-Item $out (Split-Path $backup -Leaf) -ErrorAction Stop }
        if (-not $renamed) {
            Write-Host "-- $out залочен, пробую подменить файлы поверх" -ForegroundColor Yellow
            $stuck = Copy-Over $staging $out
            if ($stuck.Count -eq 0) {
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
                Write-Host "-- $out обновлён поверх (конфиг не тронут)" -ForegroundColor Green
                return
            }
            throw "Собрано в $staging, но $out залочен (запущен hub/cli/агент из этой папки?): " +
                  "не удалось подменить $($stuck.Count) файлов ($([string]::Join(', ', ($stuck | Select-Object -First 3)))). " +
                  "Закрой процесс и запусти сборку ещё раз. Старая версия в $out цела."
        }
    }
    Move-Item $staging $out
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
}
# Три компонента независимы (напр. hub может быть живым сервером и залоченным, пока мы
# правим только agent/cli) — сбой одного не должен мешать пересобрать остальные. Копим
# ошибки и валимся с сводкой только в самом конце, после того как попробовали все три.
$failed = @()
$staleDirs = @()   # компоненты, оставшиеся на старом бинаре: их конфиг трогать нельзя
foreach ($p in @(
    @{ Project = "src/SzDiag.Hub"; Out = "dist/host/hub" },
    @{ Project = "src/SzDiag.Cli"; Out = "dist/host/cli" },
    @{ Project = "src/SzDiag.Agent"; Out = "dist/client" }
)) {
    try { Publish $p.Project $p.Out }
    catch {
        Write-Host "-- ПРОПУСК $($p.Project): $($_.Exception.Message)" -ForegroundColor Yellow
        $failed += $p.Project
        $staleDirs += $p.Out
    }
}

# Конфиг пересобранного компонента переписываем, непересобранного — нет. Иначе получается
# рассинхрон «конфиг новый, exe старый»: ровно так hub 05.08 крутил билд пятичасовой давности
# со свежим appsettings.json, и по времени файлов это выглядело как каша (бэклог п.51).
function Should-WriteConfig($outDir) {
    $normalized = $outDir.Replace('/', '\')
    $isStale = $staleDirs | Where-Object { $_.Replace('/', '\') -ieq $normalized }
    if (-not $isStale) { return $true }
    $cfg = Join-Path $outDir "appsettings.json"
    if (-not (Test-Path $cfg)) { return $true }   # конфига вообще нет — писать безопасно
    Write-Host "-- конфиг $cfg НЕ переписан: компонент остался на старой сборке" -ForegroundColor Yellow
    return $false
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
    "ToolsRoot": "$(("$root\client-tools").Replace('\','\\'))",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15",
    "KbBackup": {
      "Enabled": true,
      "Interval": "00:15:00",
      "Remote": "origin",
      "Branch": "main",
      "CommandTimeout": "00:02:00"
    }
  }
}
"@
# Пишем конфиг только если папка компонента реально существует — если его publish выше
# провалился и старой копии тоже никогда не было (напр. самый первый запуск), писать некуда.
if ((Test-Path dist\host\hub) -and (Should-WriteConfig "dist/host/hub")) {
    Set-Content -Path dist\host\hub\appsettings.json -Value $hubCfg -Encoding utf8
}

$cliCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb"
}
"@
if ((Test-Path dist\host\cli) -and (Should-WriteConfig "dist/host/cli")) {
    Set-Content -Path dist\host\cli\appsettings.json -Value $cliCfg -Encoding utf8
}

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
if ((Test-Path dist\client) -and (Should-WriteConfig "dist/client")) {
    Set-Content -Path dist\client\appsettings.json -Value $agentCfg -Encoding utf8
}

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
    # Явно и громко: «готово частично» раньше читалось как «готово», и правка молча не
    # доезжала до рантайма — hub крутил старый билд, а по логу сборки всё выглядело нормально.
    Write-Host "==================================================================" -ForegroundColor Red
    Write-Host "  ВНИМАНИЕ: РАНТАЙМ ОСТАЛСЯ НА СТАРОЙ ВЕРСИИ" -ForegroundColor Red
    Write-Host "  Не пересобрались: $($failed -join ', ')" -ForegroundColor Red
    Write-Host "  Их конфиги не переписаны (чтобы не было 'конфиг новый, exe старый')." -ForegroundColor Red
    Write-Host "  Закрой процессы этих компонентов и запусти сборку ещё раз." -ForegroundColor Red
    Write-Host "==================================================================" -ForegroundColor Red
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
