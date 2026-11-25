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

.PARAMETER ToolsRoot
  Каталог, из которого hub раздаёт инструменты клиенту (`szcli push`). По умолчанию — общий
  каталог сервиса `C:\Share\Tests\client\client-tools`, если он существует, иначе
  `<репо>\client-tools`. Последний лежит в .gitignore и на свежем клоне почти пуст (только
  портативный ssh) — из-за этого `push --list` показывал один инструмент, и приходилось руками
  делать junction (бэклог п.67):
    .\tools\build-dist.ps1 -ToolsRoot D:\tools\client-tools

.PARAMETER WatchdogHours
  Через сколько часов watchdog-задача откатит доступ на клиенте (по умолчанию 6, как
  дефолт в AgentOptions). Задача ставится на фиксированное время при открытии доступа и
  активностью агента НЕ продлевается (бэклог п.81) — на 160306 час съел доступ прямо
  посреди работы. Для долгих прогонов ставь с запасом:
    .\tools\build-dist.ps1 -WatchdogHours 12
#>
param(
    [string]$HubIp = "",
    [int]$Port = 5099,
    # Устарело: токены берутся из секретов флота (secrets\fleet.json, tools\fleet.ps1).
    # Заданный и отличный от флотского — игнорируется с предупреждением.
    [string]$Token = "",
    [string]$ToolsRoot = "",
    [double]$WatchdogHours = 6,
    # Доступ к локальному API учётной системы для `szcli sz fetch`. Порта по умолчанию нет
    # намеренно: репозиторий публичный, конкретика живёт в docs\erp-api.md вне git.
    # Пустой ErpPort = команда честно скажет «адрес не задан», а не будет стучаться в никуда.
    [int]$ErpPort = 0,
    [string]$ErpTokenFile = "",
    # Постоянный адрес hub через Cloudflare Tunnel (например https://hub.example.com).
    # Задан — попадает в HubUrl клиента и становится основным путём, а UDP-broadcast
    # остаётся запасным. Значения по умолчанию нет намеренно: репозиторий публичный,
    # реальный домен в git не хранится.
    [string]$HubUrl = "",
    # Service token приложения Cloudflare Access перед hub (пара CF-Access-*).
    [string]$AccessClientId = "",
    [string]$AccessClientSecret = "",
    # Путь к cloudflared.exe на клиенте. Пусто — туннель не поднимается (прямой режим);
    # бинарь приезжает на клиента через `szcli push cloudflared`.
    [string]$ClientCloudflaredPath = "tools\cloudflared\cloudflared.exe",
    # Именованный туннель, публикующий САМ hub наружу (hub.<домен>). Hub поднимает cloudflared
    # на старте и снимает при остановке — служба/автозапуск по входу не нужны. Пусто — берётся
    # из уже существующего dist\host\hub\appsettings.json, иначе туннель выключен.
    [string]$TunnelName = "",
    [string]$TunnelConfig = "",
    # Путь к cloudflared.exe на хосте. Пусто — ищется в PATH и штатных каталогах установки.
    [string]$TunnelExe = "",
    # Свой указатель hub (specs/002): сервис szdiag-signpost в СВОЁМ проекте Railway, например
    # https://<сервис>.up.railway.app. Значения по умолчанию нет намеренно — репозиторий публичный,
    # каждый поднимает свой. Пусто — берётся из прежнего dist\host\hub\appsettings.json, иначе
    # указатель выключен и клиент ищет hub только по конфигу и в LAN.
    [string]$SignpostUrl = "",
    # Имя hub для людей в указателе и Desk («ПК», «ноут»). Пусто — имя машины.
    [string]$HubName = "",
    # Опубликовать собранный пакет агента: подписанный релиз — в свой указатель. На GitHub
    # ничего не уходит: исполняемые файлы в репозитории не релизятся.
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Каталог раздачи инструментов: явный параметр → общий каталог сервиса → папка в репо.
# Дефолт «репо» и приводил к пустому `szcli push --list` на свежем боксе (бэклог п.67).
if ([string]::IsNullOrWhiteSpace($ToolsRoot)) {
    $shareTools = "C:\Share\Tests\client\client-tools"
    $ToolsRoot = if (Test-Path $shareTools) { $shareTools } else { "$root\client-tools" }
}
Write-Host "-- каталог инструментов (ToolsRoot): $ToolsRoot"
if (-not (Test-Path $ToolsRoot)) {
    Write-Host "   ВНИМАНИЕ: каталога нет — szcli push раздавать будет нечего." -ForegroundColor Yellow
}

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

# 1. Секреты флота: один набор на все машины-hub (ssh-ключ сервиса, токены, ключ подписи).
# Своих ключей build-dist больше не генерирует: раньше каждая машина делала свои, и апдейтер,
# собранный на ноуте, получал 401 от hub на ПК. Нет секретов — забираем из Railway, не вышло —
# стоп, а не молчаливая генерация несовместимых (specs/002-domain-independent-access, FR-013).
New-Item -ItemType Directory secrets -Force | Out-Null
if (-not (Test-Path secrets\fleet.json)) {
    Write-Host "-- нет secrets\fleet.json — забираю секреты флота из Railway (tools\fleet.ps1 pull)"
    & (Join-Path $PSScriptRoot 'fleet.ps1') pull
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path secrets\fleet.json)) {
        Write-Host "build-dist: секретов флота нет — сборка остановлена." -ForegroundColor Red
        Write-Host "  Первая машина флота: .\tools\fleet.ps1 init   ·   остальные: .\tools\fleet.ps1 pull" -ForegroundColor Red
        exit 6
    }
}
foreach ($f in 'svc_diag_key', 'svc_diag_key.pub', 'fleet-sign.pub', 'fleet-sign.key') {
    if (-not (Test-Path "secrets\$f")) {
        Write-Host "build-dist: в secrets\ нет $f — секреты флота неполные, выполни .\tools\fleet.ps1 pull" -ForegroundColor Red
        exit 6
    }
}
$fleet = Get-Content secrets\fleet.json -Raw | ConvertFrom-Json
Write-Host "-- секреты флота: secrets\fleet.json"

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
    # Всё, чего нет в свежей публикации, — данные рядом с exe (Desk: реестр и журналы сессий,
    # счётчик токенов; hub: known_hosts, logs, pulled). Без переноса пересборка dist молча
    # стирала их: на 160176 Desk потерял ленту и связку с разговором Claude (бэклог п.268).
    if (Test-Path $backup) {
        foreach ($item in Get-ChildItem $backup -Force) {
            if (-not (Test-Path (Join-Path $staging $item.Name))) {
                Copy-Item $item.FullName (Join-Path $staging $item.Name) -Recurse -Force
            }
        }
    }
    Move-Item $staging $out
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
}
# Компоненты независимы (напр. hub может быть живым сервером и залоченным, пока мы
# правим только agent/cli) — сбой одного не должен мешать пересобрать остальные. Копим
# ошибки и валимся с сводкой только в самом конце, после того как попробовали все.
$failed = @()
$staleDirs = @()   # компоненты, оставшиеся на старом бинаре: их конфиг трогать нельзя

# Прежние конфиги снимаем ДО публикации: Publish подменяет папку целиком, и после неё в
# dist лежит appsettings.json из проекта (туннель выключен, токен по умолчанию). Раньше
# «перенос из прежнего конфига» читал уже его — и туннель/токен молча терялись (бэклог п.265).
$prevHubCfgText = if (Test-Path dist\host\hub\appsettings.json) { Get-Content dist\host\hub\appsettings.json -Raw } else { $null }
$prevClientCfgText = if (Test-Path dist\client\appsettings.json) { Get-Content dist\client\appsettings.json -Raw } else { $null }
$prevCliCfgText = if (Test-Path dist\host\cli\appsettings.json) { Get-Content dist\host\cli\appsettings.json -Raw } else { $null }

foreach ($p in @(
    @{ Project = "src/SzDiag.Hub"; Out = "dist/host/hub" },
    @{ Project = "src/SzDiag.Cli"; Out = "dist/host/cli" },
    @{ Project = "src/SzDiag.Desk"; Out = "dist/host/desk" },
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
    Copy-Item secrets\fleet-sign.pub dist\client\fleet-sign.pub -Force
}

# 2c. cloudflared для публикации sshd клиента quick tunnel'ом. Кладём в каталог раздачи,
# чтобы он ехал на клиента по требованию через `szcli push cloudflared`, а не в пакете
# агента: иначе апдейтер потолстеет на полсотни мегабайт ради случая, нужного не всегда.
# Отсутствие — не ошибка: без него работает прямой режим (машина в сети бокса).
$cloudflaredSrc = @(
    "$env:ProgramFiles\cloudflared\cloudflared.exe",
    "${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($cloudflaredSrc) {
    $cfDst = Join-Path $ToolsRoot "cloudflared"
    New-Item -ItemType Directory $cfDst -Force | Out-Null
    Copy-Item $cloudflaredSrc $cfDst -Force
    Write-Host "-- cloudflared -> $cfDst (раздача через szcli push cloudflared)"
} else {
    Write-Host "   cloudflared не найден на боксе — туннельный режим будет недоступен," -ForegroundColor Yellow
    Write-Host "   пока бинарь не попадёт в $ToolsRoot\cloudflared\ (прямой режим работает)." -ForegroundColor Yellow
}

# Портативный sshd рядом с агентом: dist\client\ssh
if (Test-Path dist\client\SzDiag.Agent.exe) {
    Write-Host "-- копирую OpenSSH -> dist\client\ssh"
    New-Item -ItemType Directory dist\client\ssh -Force | Out-Null
    Copy-Item "$sshCache\sshd.exe","$sshCache\ssh-keygen.exe","$sshCache\sftp-server.exe" dist\client\ssh\ -Force
    # dll-зависимости (libcrypto и пр.) лежат рядом с exe в релизе — берём все dll.
    Copy-Item "$sshCache\*.dll" dist\client\ssh\ -Force -ErrorAction SilentlyContinue
}

# Апдейтер публикуется НИЖЕ, после записи dist\client\appsettings.json: конфиг клиента вшивается в exe,
# чтобы на клиент хватало одного файла.

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
    # Время сборки — порядок релизов для апдейтера (git sha не упорядочен): подписанный релиз
    # старее установленного не ставится (защита от replay, specs/002 R6).
    $buildTime = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $buildInfo = '{"version":"' + $version + '","buildTime":"' + $buildTime + '"}'
    [System.IO.File]::WriteAllText((Join-Path $root 'dist\client\build-info.json'), $buildInfo)

    # Пакет: agent.exe + ssh + service_key.pub + testsuite.json + version.txt.
    # БЕЗ appsettings.json (локальный конфиг клиента), tools\ (тяжёлые тулы) и Updater.exe.
    $pkgStage = Join-Path $env:TEMP "szdiag-pkg"
    Remove-Item $pkgStage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $pkgStage -Force | Out-Null
    Copy-Item dist\client\SzDiag.Agent.exe    $pkgStage\ -Force
    # libzt (ZeroTier, US3) — нативная, в single-file не вшивается: без неё агент работает без ZT.
    Copy-Item dist\client\libzt.dll           $pkgStage\ -Force -ErrorAction SilentlyContinue
    Copy-Item dist\client\service_key.pub     $pkgStage\ -Force -ErrorAction SilentlyContinue
    Copy-Item dist\client\fleet-sign.pub      $pkgStage\ -Force -ErrorAction SilentlyContinue
    Copy-Item dist\client\testsuite.json      $pkgStage\ -Force -ErrorAction SilentlyContinue
    Copy-Item dist\client\version.txt         $pkgStage\ -Force
    Copy-Item dist\client\build-info.json     $pkgStage\ -Force
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

# Туннель хаба: hub сам поднимает cloudflared на старте и снимает при остановке.
# Параметры не заданы — сохраняем то, что уже настроено в существующем конфиге, иначе
# пересборка молча оставила бы hub без внешнего адреса (та же грабля, что с appsettings).
$tunnelName = $TunnelName
$tunnelConfig = $TunnelConfig
$tunnelExe = $TunnelExe
$prevHub = $null
if ($prevHubCfgText) {
    try { $prevHub = ($prevHubCfgText | ConvertFrom-Json).Hub } catch {
        Write-Host "   ВНИМАНИЕ: прежний appsettings.json хаба не разобран — туннель и токен не перенесены." -ForegroundColor Yellow
    }
}
if (-not $tunnelName -and -not $tunnelConfig -and $prevHub) {
    try {
        $prev = $prevHub.Tunnel
        if ($prev) {
            $tunnelName   = [string]$prev.Name
            $tunnelConfig = [string]$prev.ConfigPath
            $tunnelExe    = [string]$prev.ExecutablePath
        }
    } catch {
        Write-Host "   ВНИМАНИЕ: прежний appsettings.json хаба не разобран — секция Tunnel не перенесена." -ForegroundColor Yellow
    }
}
# Токен и постоянный адрес hub тоже переносим из прежнего dist: пересборка без -Token молча
# ставила "dev-token", и апдейтер на клиенте с настоящим токеном получал 401 — а без HubUrl
# клиент из новой сборки терял внешний адрес (бэклог п.265).
# Токены — из секретов флота: одинаковые на всех машинах, поэтому апдейтер из любой сборки
# принимается любым hub. -Token больше не главнее флота.
if ($Token -and $Token -ne [string]$fleet.agentToken) {
    Write-Host "-- -Token проигнорирован: токен агента берётся из секретов флота" -ForegroundColor Yellow
}
$Token = [string]$fleet.agentToken
$MgmtToken = [string]$fleet.managementToken
if (-not $MgmtToken) { $MgmtToken = $Token }
# Токены соседних hub (MacBook, рабочий ноут…): апдейтер, собранный на любом из них, принимается
# и здесь. Источник — secrets\agent-tokens.txt (по токену на строку, # — комментарий) плюс то,
# что уже было в прежнем конфиге: пересборка не теряет однажды добавленных соседей.
$extraTokens = @()
if ($prevHub -and $prevHub.ExtraAgentTokens) { $extraTokens += @($prevHub.ExtraAgentTokens | ForEach-Object { [string]$_ }) }
# Переход на флот: прежний токен этой машины уже лежит в разданных апдейтерах — принимаем и его.
if ($prevHub -and $prevHub.AgentToken -and [string]$prevHub.AgentToken -ne 'dev-token') { $extraTokens += [string]$prevHub.AgentToken }
$extraTokensFile = Join-Path $root 'secrets\agent-tokens.txt'
if (Test-Path $extraTokensFile) {
    $extraTokens += @(Get-Content $extraTokensFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}
$extraTokens = @($extraTokens | Where-Object { $_ -and $_ -ne $Token } | Select-Object -Unique)
$extraTokensJson = ($extraTokens | ForEach-Object { '"' + ($_ -replace '\\', '\\' -replace '"', '\"') + '"' }) -join ', '
Write-Host "-- соседские токены агентов: $($extraTokens.Count) (secrets\agent-tokens.txt)"
# С указателем домен туннеля клиенту не нужен (specs/002): клиент найдёт hub сам — LAN, затем
# указатель (zt/lan). Без этого прежний https-адрес переносился из старого конфига вечно, и убрать
# домен из апдейтера можно было только руками. Явный -HubUrl по-прежнему главнее.
$signpostKnown = $SignpostUrl -or ($prevHub -and $prevHub.Signpost -and $prevHub.Signpost.Url)
if ($signpostKnown -and -not $HubUrl -and $prevClientCfgText) {
    Write-Host "-- указатель задан: прежний HubUrl клиента не переносится (поиск hub — LAN → указатель)"
}
if (-not $HubUrl -and -not $HubIp -and -not $signpostKnown -and $prevClientCfgText) {
    try {
        $prevClient = $prevClientCfgText | ConvertFrom-Json
        # Только постоянный адрес (домен туннеля): http://<ip> прошлой сборки мог устареть.
        if ([string]$prevClient.HubUrl -like 'https://*') {
            $HubUrl = [string]$prevClient.HubUrl
            if (-not $AccessClientId) {
                $AccessClientId = [string]$prevClient.AccessClientId
                $AccessClientSecret = [string]$prevClient.AccessClientSecret
            }
        }
    } catch { }
}
Write-Host "-- токен: из секретов флота; HubUrl клиента: $(if ($HubUrl) { $HubUrl } else { 'нет (UDP-обнаружение)' })"
$tunnelEnabled = if ($tunnelName -or $tunnelConfig) { "true" } else { "false" }
Write-Host "-- туннель хаба: $(if ($tunnelEnabled -eq 'true') { "$tunnelName ($tunnelConfig)" } else { 'выключен' })"

# Адрес своего указателя переносим из прежнего конфига hub — как токены и туннель.
if (-not $SignpostUrl -and $prevHub -and $prevHub.Signpost -and $prevHub.Signpost.Url) {
    $SignpostUrl = [string]$prevHub.Signpost.Url
}
$SignpostUrl = $SignpostUrl.TrimEnd('/')
if (-not $HubName -and $prevHub -and $prevHub.Signpost -and $prevHub.Signpost.HubName) {
    $HubName = [string]$prevHub.Signpost.HubName
}
$signpostEnabled = if ($SignpostUrl) { "true" } else { "false" }
$signKeyAbs = (Join-Path $root 'secrets\fleet-sign.key').Replace('\', '\\')
# Указатель публичен: в него уходят только адреса, которые hub найдёт сам (LAN, позже ZeroTier).
# Домены туннеля сюда не пишем никогда.
Write-Host "-- указатель: $(if ($SignpostUrl) { $SignpostUrl } else { 'выключен (-SignpostUrl https://<свой>.up.railway.app)' })"

# Сервисная ZT-сеть — из флота (tools\zt-network.ps1 → fleet.ps1 push). Нет сети — hub без ZT.
$ztNetwork = [string]$fleet.ztNetwork
$ztEnabled = if ($ztNetwork) { "true" } else { "false" }
Write-Host "-- ZeroTier: $(if ($ztNetwork) { "сеть $ztNetwork" } else { 'не настроен (tools\zt-network.ps1)' })"

# Hub.DenySubnets — личные ZT-сети этой машины, на адреса которых hub не отвечает вовсе.
# В коде дефолт пустой (личная подсеть владельца не живёт в публичном репо, а массив из
# конфига к непустому дефолту только дописывается), поэтому список собирается здесь:
# всё, что было в прежнем конфиге, плюс подсети всех ZT-сетей ZeroTier One, кроме сервисной.
$denySubnets = @()
if ($prevHub -and $prevHub.DenySubnets) { $denySubnets += @($prevHub.DenySubnets | ForEach-Object { [string]$_ }) }
$ztAuthFile = Join-Path $env:LOCALAPPDATA 'ZeroTier\authtoken.secret'
if (Test-Path $ztAuthFile) {
    $ztPortFile = Join-Path $env:ProgramData 'ZeroTier\One\zerotier-one.port'
    $ztPort = if (Test-Path $ztPortFile) { (Get-Content $ztPortFile -Raw).Trim() } else { '9993' }
    try {
        $ztHeaders = @{ 'X-ZT1-Auth' = (Get-Content $ztAuthFile -Raw).Trim() }
        # Через конвейер: в 5.1 Invoke-RestMethod отдаёт JSON-массив одним объектом, @() его не разворачивает.
        $ztNets = @(Invoke-RestMethod "http://127.0.0.1:$ztPort/network" -Headers $ztHeaders -TimeoutSec 5 | ForEach-Object { $_ })
        foreach ($n in @($ztNets | Where-Object { [string]$_.id -ne $ztNetwork })) {
            foreach ($a in @($n.assignedAddresses)) {
                if ([string]$a -notmatch '^(\d{1,3}(?:\.\d{1,3}){3})/(\d{1,2})$') { continue }   # IPv6 — мимо
                $ip = [System.Net.IPAddress]::Parse($Matches[1])
                $prefix = [int]$Matches[2]
                if ($prefix -gt 32) { continue }
                $b = $ip.GetAddressBytes()
                # [uint64]: в 5.1 литерал 0xFFFFFFFF — это -1 (int32), маска на нём ломается.
                $ipNum = ([uint64]$b[0] -shl 24) -bor ([uint64]$b[1] -shl 16) -bor ([uint64]$b[2] -shl 8) -bor [uint64]$b[3]
                $mask = if ($prefix -eq 0) { [uint64]0 } else { ([uint64]4294967295 -shl (32 - $prefix)) -band [uint64]4294967295 }
                $netNum = $ipNum -band $mask
                $netIp = '{0}.{1}.{2}.{3}' -f (($netNum -shr 24) -band 255), (($netNum -shr 16) -band 255), (($netNum -shr 8) -band 255), ($netNum -band 255)
                $denySubnets += "$netIp/$prefix"
            }
        }
    } catch {
        Write-Host "-- ZeroTier One не ответил ($($_.Exception.Message)) — DenySubnets только из прежнего конфига" -ForegroundColor Yellow
    }
}
$denySubnets = @($denySubnets | Where-Object { $_ } | Select-Object -Unique)
$denySubnetsJson = ($denySubnets | ForEach-Object { '"' + $_ + '"' }) -join ', '
Write-Host "-- DenySubnets (личные ZT-сети): $(if ($denySubnets) { $denySubnets -join ', ' } else { 'нет' })"

$hubCfg = @"
{
  "Urls": "http://0.0.0.0:$Port",
  "Hub": {
    "AgentToken": "$Token",
    "ExtraAgentTokens": [$extraTokensJson],
    "ManagementToken": "$MgmtToken",
    "Port": $Port,
    "SqliteConnectionString": "Data Source=$db",
    "KnowledgeBaseRoot": "$kb",
    "AgentDistRoot": "$(("$root\dist\host\hub\agent-dist").Replace('\','\\'))",
    "ToolsRoot": "$($ToolsRoot.Replace('\','\\'))",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15",
    "KbBackup": {
      "Enabled": true,
      "Interval": "00:15:00",
      "Remote": "origin",
      "Branch": "main",
      "CommandTimeout": "00:02:00"
    },
    "Tunnel": {
      "Enabled": $tunnelEnabled,
      "Name": "$tunnelName",
      "ConfigPath": "$($tunnelConfig.Replace('\','\\'))",
      "ExecutablePath": "$($tunnelExe.Replace('\','\\'))",
      "RestartDelay": "00:00:10",
      "PidFile": "cloudflared.pid"
    },
    "Signpost": {
      "Enabled": $signpostEnabled,
      "Url": "$SignpostUrl",
      "HubName": "$HubName",
      "SignKeyPath": "$signKeyAbs",
      "PublishInterval": "00:00:30",
      "ExtraEndpoints": []
    },
    "DenySubnets": [$denySubnetsJson],
    "ZeroTier": {
      "Enabled": $ztEnabled,
      "Network": "$ztNetwork",
      "Subnet": "10.88.0.0/24",
      "SweepAfter": "00:10:00",
      "ControllerUrl": "$([string]$fleet.ztControllerUrl)",
      "ControllerToken": "$([string]$fleet.ztControllerToken)",
      "HubAddresses": ["10.88.0.1", "10.88.0.2"]
    }
  }
}
"@
# Пишем конфиг только если папка компонента реально существует — если его publish выше
# провалился и старой копии тоже никогда не было (напр. самый первый запуск), писать некуда.
if ((Test-Path dist\host\hub) -and (Should-WriteConfig "dist/host/hub")) {
    Set-Content -Path dist\host\hub\appsettings.json -Value $hubCfg -Encoding utf8
}

# Абсолютный путь к ключу — чтобы `szcli target` печатал команду, работающую с первого
# раза, а не `ssh user@ip` без -i (бэклог п.118).
$sshKeyAbs = (Resolve-Path secrets\svc_diag_key).Path -replace '\\', '\\'
$erpBaseUrl = if ($ErpPort -gt 0) { "http://127.0.0.1:$ErpPort" } else { "" }
$erpTokenFile = $ErpTokenFile
# Секцию Erp без параметров переносим из прежнего конфига: пересборка без -ErpPort молча
# обнуляла её, и `sz get`/`sz fetch` падали exit 3 уже на живой заявке (бэклог п.244, п.285).
if ($prevCliCfgText -and (-not $erpBaseUrl -or -not $erpTokenFile)) {
    try {
        $prevErp = ($prevCliCfgText | ConvertFrom-Json).Erp
        if ($prevErp) {
            if (-not $erpBaseUrl)   { $erpBaseUrl   = [string]$prevErp.BaseUrl }
            if (-not $erpTokenFile) { $erpTokenFile = [string]$prevErp.TokenFile }
        }
    } catch {
        Write-Host "   ВНИМАНИЕ: прежний appsettings.json szcli не разобран — секция Erp не перенесена." -ForegroundColor Yellow
    }
}
if ($erpBaseUrl) { Write-Host "-- ERP: $erpBaseUrl" }
else { Write-Host "-- ERP не настроен: sz get/fetch дадут exit 3 — пересобери с -ErpPort <порт> -ErpTokenFile <путь> (порт: Get-NetTCPConnection -State Listen по процессу TeleAuto)" -ForegroundColor Yellow }
$erpToken = $erpTokenFile -replace '\\', '\\'
$cliCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$MgmtToken",
  "KbRoot": "$kb",
  "SshKeyPath": "$sshKeyAbs",
  "Erp": {
    "BaseUrl": "$erpBaseUrl",
    "TokenFile": "$erpToken",
    "TimeoutSeconds": 600
  }
}
"@
if ((Test-Path dist\host\cli) -and (Should-WriteConfig "dist/host/cli")) {
    Set-Content -Path dist\host\cli\appsettings.json -Value $cliCfg -Encoding utf8
}

# Desk — тот же hub и тот же токен, что у CLI: окно и szcli обязаны видеть одно и то же.
# Сессии Claude работают из корня репозитория (CLAUDE.md, скиллы). Профили Claude (~/.claude,
# ~/.claude2 …) Desk ищет сам и даёт выбрать на заявку — в конфиг их не пишем: окружение того,
# кто собирал dist, ничего не говорит о том, каким профилем вести конкретную заявку.
$claudeWork = ("$root").Replace('\', '\\')
$deskCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$MgmtToken",
  "KbRoot": "$kb",
  "ClaudeWorkDir": "$claudeWork",
  "PermissionMode": "auto"
}
"@
if ((Test-Path dist\host\desk) -and (Should-WriteConfig "dist/host/desk")) {
    Set-Content -Path dist\host\desk\appsettings.json -Value $deskCfg -Encoding utf8
}

# Явный -HubUrl (домен через Cloudflare Tunnel) главнее -HubIp: это постоянный адрес,
# который работает из любой сети, тогда как IP годится только внутри LAN сервиса.
$hubUrlValue = if (-not [string]::IsNullOrWhiteSpace($HubUrl)) { $HubUrl }
               elseif ([string]::IsNullOrWhiteSpace($HubIp)) { "" }
               else { "http://$($HubIp):$($Port)" }
# Через InvariantCulture: на локали с запятой "1.5" превратилось бы в "1,5" и порвало JSON.
$watchdogValue = $WatchdogHours.ToString([System.Globalization.CultureInfo]::InvariantCulture)

# JSON не терпит одиночный обратный слеш — путь к cloudflared экранируем.
$clientCloudflaredJson = $ClientCloudflaredPath.Replace('\', '\\')
# Публичный ключ подписи флота — им клиент проверяет записи указателя (US1). Приватная
# половина (fleet-sign.key) на клиента не уезжает никогда — проверка в конце сборки.
$signPubJson = ((Get-Content secrets\fleet-sign.pub -Raw).Trim() -replace "`r?`n", '\n')
$agentCfg = @"
{
  "HubUrl": "$hubUrlValue",
  "AgentToken": "$Token",
  "SignPublicKey": "$signPubJson",
  "SignpostUrl": "$SignpostUrl",
  "AccessClientId": "$AccessClientId",
  "AccessClientSecret": "$AccessClientSecret",
  "CloudflaredPath": "$clientCloudflaredJson",
  "ServiceAccount": "svc-diag",
  "ServicePublicKeyPath": "service_key.pub",
  "SshPort": 22,
  "WatchdogHours": $watchdogValue,
  "HeartbeatSeconds": 15,
  "StatePath": "C:\\ProgramData\\szdiag\\state.json",
  "TestSuitePath": "testsuite.json"
}
"@
if ((Test-Path dist\client) -and (Should-WriteConfig "dist/client")) {
    Set-Content -Path dist\client\appsettings.json -Value $agentCfg -Encoding utf8
}

# Апдейтер — рядом с агентом в dist\client (та же папка, БЕЗ сноса — агент уже там, а функция Publish
# меняет папку целиком через staging и снесла бы его). Прямой publish -o. Конфиг клиента вшивается в
# exe ресурсом: на клиент приносится ОДИН SzDiag.Updater.exe, appsettings.json он кладёт агенту сам.
# Файл-источник живёт только на время publish (в .gitignore — внутри токен агента).
if (Test-Path dist\client\SzDiag.Agent.exe) {
    $embeddedCfg = Join-Path $root 'src\SzDiag.Updater\embedded-client.json'
    $cfgForExe = if (Test-Path dist\client\appsettings.json) { Get-Content dist\client\appsettings.json -Raw -Encoding UTF8 } else { $agentCfg }
    [System.IO.File]::WriteAllText($embeddedCfg, $cfgForExe, (New-Object System.Text.UTF8Encoding($false)))
    try {
        Write-Host "-- публикую SzDiag.Updater -> dist\client (конфиг клиента вшит в exe)"
        dotnet publish src/SzDiag.Updater @common -o dist\client | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish SzDiag.Updater упал (код $LASTEXITCODE)." }
    } finally {
        Remove-Item $embeddedCfg -Force -ErrorAction SilentlyContinue
    }
}

# 4. Удобные лаунчеры на хосте (single-quoted here-string — литералы)
# exe — полным путём: при NoDefaultCurrentDirectoryInExePath=1 (его ставит окружение Claude Code, а
# Desk, запущенный оттуда, передаёт дальше) cmd не ищет голое имя в текущей папке — код 9009.
$startHub = @'
@echo off
cd /d "%~dp0hub"
"%~dp0hub\SzDiag.Hub.exe" %*
pause
'@
Set-Content -Path dist\host\start-hub.cmd -Value $startHub -Encoding ascii

$szcli = @'
@echo off
"%~dp0cli\SzDiag.Cli.exe" %*
'@
Set-Content -Path dist\host\szcli.cmd -Value $szcli -Encoding ascii

# szcli.ps1 - обёртка для PowerShell-сессий (техник на боксе почти всегда в ней). У szcli.cmd
# есть грабля (бэклог п.49): PowerShell не квотирует аргумент без пробелов при релонче через
# cmd.exe, и `szcli hw resolve "PCI\VEN_10DE&DEV_..."` режется на две "команды" - `DEV_...` уже
# не находится. Обход через двойные кавычки внутри одинарных неочевиден. Вызов `.ps1` через `&`
# передаёт $args как готовый массив строк, минуя реконструкцию и разбор командной строки в
# cmd.exe - `&`/`|`/`^` в аргументе доезжают как есть.
$szcliPs1 = @'
$exe = Join-Path $PSScriptRoot 'cli\SzDiag.Cli.exe'
if (-not (Test-Path $exe)) { Write-Error "нет $exe - прогони .\build-dist.ps1"; exit 4 }
& $exe @args
# C-5 (ревью волны 2): R-M11 волны 1 чинил "exit $LASTEXITCODE" молча дающий 0 при $null через
# [Environment]::Exit - но [Environment]::Exit завершает ВЕСЬ ПРОЦЕСС хоста, а эта обёртка
# заведена ровно для интерактивных PowerShell-сессий (техник на боксе почти всегда уже в ней,
# см. комментарий выше) - "& .\szcli.ps1 list" из такой сессии убивало консоль оператора при
# КАЖДОМ вызове, включая успех. Голый "exit" внутри скрипта, вызванного через `&` из активного
# хоста, ЗАВЕРШАЕТ ТОЛЬКО ЭТОТ ВЫЗОВ и корректно прокидывает код в $LASTEXITCODE вызывающего
# (проверено: caller.ps1 после "& szcli.ps1" печатает LASTEXITCODE=5, консоль жива); при запуске
# как "powershell -File szcli.ps1" тот же "exit" завершает процесс, как и положено. $null-случай
# после Test-Path выше почти невозможен, но страховку от R-M11 оставляем.
if ($null -eq $LASTEXITCODE) { exit 1 }
exit $LASTEXITCODE
'@
Set-Content -Path dist\host\szcli.ps1 -Value $szcliPs1 -Encoding utf8

# Лаунчер-страховка на клиенте: перехватывает вывод апдейтера в файл и держит окно.
# Породила 161642: `SzDiag.Updater.exe` по двойному клику показывал окно на долю секунды,
# а `logs\updater.log` не появлялся вообще — значит процесс умирал ДО своего Main (отказ
# UAC, антивирус, битый exe) либо на клиенте лежала старая версия, ещё без логирования.
# Внутренний лог такие случаи не ловит по определению, нужен внешний перехват.
# Текст латиницей: файл в ASCII (как остальные лаунчеры), cmd.exe иначе даёт кракозябры.
$updaterLog = @'
@echo off
setlocal
set "LOG=%~dp0updater-run.txt"
set "EXE=%~dp0SzDiag.Updater.exe"

net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo [!] Net prav administratora. Zakroy eto okno i zapusti fayl
  echo     pravoy knopkoy -^> "Zapusk ot imeni administratora".
  echo.
  pause
  exit /b 1
)

echo ==== %DATE% %TIME% ==== > "%LOG%"
if not exist "%EXE%" (
  echo NET FAYLA: %EXE% >> "%LOG%"
  type "%LOG%"
  pause
  exit /b 2
)

rem Data i razmer exe: srazu vidno, staraya li eto versiya (bez logirovaniya).
for %%F in ("%EXE%") do echo exe: %%~zF bayt, %%~tF >> "%LOG%"
echo. >> "%LOG%"

"%EXE%" >> "%LOG%" 2>&1
echo. >> "%LOG%"
echo Kod vozvrata: %ERRORLEVEL% >> "%LOG%"

type "%LOG%"
echo.
echo Vyvod sohranen: %LOG%
pause
'@
if (Test-Path dist\client) {
    Set-Content -Path dist\client\updater-log.cmd -Value $updaterLog -Encoding ascii
}

# FR-014: приватные ключи флота на клиента не уезжают. Клиентский dist и пакет апдейтера —
# публичные артефакты (пакет раздаётся с указателя всем), поэтому проверяем по факту.
$privateNames = @('fleet-sign.key', 'svc_diag_key', 'fleet.json', 'agent-tokens.txt')
$leaks = @()
if (Test-Path dist\client) {
    $leaks += @(Get-ChildItem dist\client -Recurse -File | Where-Object { $privateNames -contains $_.Name } | ForEach-Object { $_.FullName })
}
$pkgZip = 'dist\host\hub\agent-dist\package.zip'
if (Test-Path $pkgZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $pkgZip).Path)
    try { $leaks += @($z.Entries | Where-Object { $privateNames -contains $_.Name } | ForEach-Object { "$pkgZip!$($_.FullName)" }) }
    finally { $z.Dispose() }
}
if ($leaks.Count -gt 0) {
    Write-Host "build-dist: в клиентский dist/пакет попали секреты флота — удали и пересобери:" -ForegroundColor Red
    $leaks | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 7
}

# -Publish: подписанный релиз агента — в свой указатель (specs/002, US1). Клиент вне LAN берёт
# пакет оттуда и сверяет sha256 с подписанным релизом. На GitHub ничего не уходит.
if ($Publish) {
    if ($failed.Count -gt 0) {
        Write-Host "-- -Publish пропущен: сборка неполная ($($failed -join ', '))" -ForegroundColor Yellow
    } elseif (-not (Test-Path $pkgZip) -or -not $buildTime) {
        # Без $buildTime пакет — от прошлой сборки: публиковать его под новой подписью нельзя.
        Write-Host "-- -Publish пропущен: пакет агента в этой сборке не собирался" -ForegroundColor Yellow
    } else {
        $cliExe = Join-Path $root 'dist\host\cli\SzDiag.Cli.exe'
        $pkgSha = (Get-FileHash $pkgZip -Algorithm SHA256).Hash.ToLower()
        if (-not $SignpostUrl) {
            Write-Host "build-dist: -Publish без указателя бессмысленен — задай -SignpostUrl https://<свой>.up.railway.app" -ForegroundColor Red
            exit 8
        }
        # Относительный адрес: клиент резолвит его от своего SignpostUrl.
        $relJson = (& $cliExe fleet sign-release $pkgZip --secrets (Join-Path $root 'secrets') `
            --version $version --build-time $buildTime --url '/package/{sha256}' | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $relJson) { Write-Host "build-dist: fleet sign-release не прошёл" -ForegroundColor Red; exit 8 }
        $relFile = Join-Path $root 'dist\host\hub\agent-dist\release.json'
        [System.IO.File]::WriteAllText($relFile, $relJson)

        if ($SignpostUrl) {
            Write-Host "-- указатель: загружаю пакет ($([math]::Round((Get-Item $pkgZip).Length / 1MB, 1)) МБ)"
            try {
                # Пакет принимается только вместе с подписанным релизом на этот же sha256.
                $relHeader = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($relJson))
                Invoke-WebRequest -UseBasicParsing -Method Put -Uri "$SignpostUrl/package/$pkgSha" -InFile $pkgZip `
                    -ContentType 'application/zip' -Headers @{ 'X-SzDiag-Release' = $relHeader } -TimeoutSec 600 | Out-Null
                Invoke-WebRequest -UseBasicParsing -Method Put -Uri "$SignpostUrl/signpost/release" -Body $relJson `
                    -ContentType 'application/json' -TimeoutSec 60 | Out-Null
                Write-Host "-- указатель: релиз $version опубликован" -ForegroundColor Green
            } catch {
                Write-Host "build-dist: указатель не принял релиз: $($_.Exception.Message)" -ForegroundColor Red
                exit 8
            }
        }
    }
}

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
Write-Host "Хост:   dist\host\   (start-hub.cmd, szcli.cmd, desk\SzDiag.Desk.exe)"
Write-Host "Клиент: dist\client\ (SzDiag.Agent.exe + ключ + testsuite, updater-log.cmd)"
Write-Host "Гайд:   docs\TESTING.md"
Write-Host "Открой порт на хосте (от админа):"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-hub-$Port -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-discovery-5098 -Direction Inbound -Protocol UDP -LocalPort 5098 -Action Allow"

if ($failed.Count -gt 0) { exit 1 }
