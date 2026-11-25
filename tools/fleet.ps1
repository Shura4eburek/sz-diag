<#
.SYNOPSIS
  Ключи флота: один набор секретов на все машины-hub (specs/002-domain-independent-access).

.DESCRIPTION
  Раньше build-dist на каждой машине генерировал свои токен агента и SSH-ключ, и апдейтер,
  собранный на ноуте, получал 401 от hub на ПК. Теперь секреты создаются один раз и хранятся
  в своём Railway (сервис szdiag-signpost в своём проекте) ТОЛЬКО зашифрованными паролем флота.
  Сервис их не читает и расшифровать не может — пароль знает только оператор.

    .\tools\fleet.ps1 init    # один раз, на машине с «правильными» секретами (обычно ПК)
    .\tools\fleet.ps1 pull    # на любой другой машине (build-dist зовёт сам, если secrets\fleet.json нет)
    .\tools\fleet.ps1 push    # перешифровать правленый secrets\fleet.json и залить (после tools\zt-network.ps1)

  Параметры ZT-сети (secrets\zt.json от tools\zt-network.ps1) init подхватывает сам, push — дописывает.

  init берёт за основу то, что уже есть на этой машине: прежний secrets\svc_diag_key и прежний
  AgentToken из dist\host\hub\appsettings.json — уже разданные с неё апдейтеры продолжают работать.

  Нужен залогиненный и слинкованный railway CLI:  railway login;  railway link  (свой проект с сервисом szdiag-signpost).
  Пароль флота — запросом без эха или переменной SZDIAG_FLEET_PASSWORD.

  Коды: 0 ок · 2 бандл уже есть (init без -Force) · 3 нет railway-логина/линка или бандла ·
        4 неверный пароль флота · 5 бандл испорчен · 1 прочее
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('init', 'pull', 'push')]
    [string]$Action,
    [string]$Service = 'szdiag-signpost',
    # init: перезаписать уже лежащий в Railway бандл (новый ключ подписи — старые записи станут чужими).
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$secrets = Join-Path $root 'secrets'
$bundleVar = 'SZDIAG_FLEET_BUNDLE'
$pubVar = 'SZDIAG_SIGN_PUBKEY'

function Fail([int]$code, [string]$msg) {
    Write-Host "fleet: $msg" -ForegroundColor Red
    exit $code
}

function Get-RailwayVars {
    if (-not (Get-Command railway -ErrorAction SilentlyContinue)) {
        Fail 3 'railway CLI не найден (npm i -g @railway/cli или scoop install railway)'
    }
    # Только stdout и EAP=Continue: railway пишет в stderr подсказки («IMPORTANT: …»), а в
    # Windows PowerShell 5.1 строка stderr нативной команды при Stop становится исключением.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $null = railway whoami 2>$null
        if ($LASTEXITCODE -ne 0) { Fail 3 'railway не залогинен: railway login' }
        $json = railway variable list --service $Service --json 2>$null
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $eap }
    if ($code -ne 0) {
        Fail 3 "не читаются переменные сервиса $Service. Слинкуй проект из корня репо: railway link (свой проект с сервисом $Service)"
    }
    try { return (($json | Out-String) | ConvertFrom-Json) }
    catch { Fail 3 "ответ railway variable list не разобран: $($_.Exception.Message)" }
}

function Set-RailwayVar([string]$name, [string]$value) {
    # Через stdin: значение не светится в командной строке и в истории процессов.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $value | railway variable set $name --stdin --service $Service --skip-deploys 2>$null | Out-Null
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $eap }
    if ($code -ne 0) { Fail 1 "не удалось записать переменную $name в Railway" }
}

# szcli собирается во временную папку и зовётся напрямую: `dotnet run` пишет вывод сборки
# в stdout и испортил бы бандл, который читается из stdout.
function Get-FleetCli {
    $out = Join-Path $env:TEMP 'szdiag-fleet-cli'
    Write-Host '-- собираю szcli для работы с ключами флота'
    dotnet build (Join-Path $root 'src\SzDiag.Cli\SzDiag.Cli.csproj') -c Release -o $out -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 1 'не собрался szcli (dotnet build src\SzDiag.Cli)' }
    return (Join-Path $out 'SzDiag.Cli.exe')
}

function Get-PrevAgentToken {
    $cfg = Join-Path $root 'dist\host\hub\appsettings.json'
    if (-not (Test-Path $cfg)) { return '' }
    try {
        $t = [string]((Get-Content $cfg -Raw | ConvertFrom-Json).Hub.AgentToken)
        if ($t -and $t -ne 'dev-token') { return $t }
    } catch { }
    return ''
}

New-Item -ItemType Directory $secrets -Force | Out-Null
Push-Location $root
try {

if ($Action -eq 'init') {
    $vars = Get-RailwayVars
    if ($vars.PSObject.Properties.Name -contains $bundleVar -and -not $Force) {
        Fail 2 "в Railway уже есть $bundleVar — флот создан. На этой машине нужен .\tools\fleet.ps1 pull (перезапись — -Force)"
    }
    $cli = Get-FleetCli

    if (-not (Test-Path (Join-Path $secrets 'fleet.json')) -or $Force) {
        if (-not (Test-Path (Join-Path $secrets 'svc_diag_key'))) {
            Write-Host '-- генерирую ssh-ключ сервиса secrets\svc_diag_key'
            # Через cmd: PowerShell ломает пустой пароль (-N '""' даёт литеральные кавычки).
            Push-Location $root
            try { cmd /c 'ssh-keygen -t ed25519 -f secrets\svc_diag_key -C szdiag-service -N "" -q' } finally { Pop-Location }
        } else {
            Write-Host '-- беру прежний ssh-ключ этой машины (secrets\svc_diag_key)'
        }
        $keygenArgs = @('fleet', 'keygen', '--secrets', $secrets)
        $prev = Get-PrevAgentToken
        if ($prev) {
            Write-Host '-- токен агента флота = прежний токен этой машины (разданные апдейтеры не ломаются)'
            $keygenArgs += @('--agent-token', $prev)
        }
        $ztJson = Join-Path $secrets 'zt.json'
        if (Test-Path $ztJson) {
            Write-Host '-- беру параметры ZT-сети из secrets\zt.json (tools\zt-network.ps1)'
            $keygenArgs += @('--zt-from', $ztJson)
        }
        if ($Force) { $keygenArgs += '--force' }
        & $cli @keygenArgs
        if ($LASTEXITCODE -ne 0) { Fail $LASTEXITCODE 'fleet keygen не прошёл' }
    } else {
        Write-Host '-- secrets\fleet.json уже есть — шифрую его как есть'
    }

    Write-Host '-- шифрую паролем флота (минимум 12 символов; потеряешь пароль — флот придётся пересоздать)'
    $bundle = (& $cli fleet encrypt --secrets $secrets | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $bundle) { Fail 1 'fleet encrypt не прошёл' }

    Set-RailwayVar $bundleVar $bundle
    Set-RailwayVar $pubVar ((Get-Content (Join-Path $secrets 'fleet-sign.pub') -Raw).Trim())
    # Публичный ключ указатель читает при старте: без передеплоя он остаётся «misconfigured»
    # и не принимает записи hub (2026-11-25: hub висел в misconfigured до ручного redeploy).
    Write-Host "-- передеплой $Service, чтобы он подхватил $pubVar"
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { railway redeploy --service $Service -y 2>$null | Out-Null; $rc = $LASTEXITCODE } finally { $ErrorActionPreference = $eap }
    if ($rc -ne 0) { Write-Host "   не удалось — сделай вручную: railway redeploy --service $Service -y" -ForegroundColor Yellow }
    Write-Host "-- готово: $bundleVar и $pubVar записаны в сервис $Service" -ForegroundColor Green
    exit 0
}

if ($Action -eq 'push') {
    # Флот уже создан, правится только содержимое (параметры ZT и т.п.). init -Force тут не
    # годится: он сгенерировал бы новый ключ подписи и обесценил записи указателя.
    if (-not (Test-Path (Join-Path $secrets 'fleet.json'))) { Fail 3 'нет secrets\fleet.json — сначала init или pull' }
    $null = Get-RailwayVars
    $cli = Get-FleetCli
    $ztJson = Join-Path $secrets 'zt.json'
    if (Test-Path $ztJson) {
        & $cli fleet set-zt --secrets $secrets --from $ztJson
        if ($LASTEXITCODE -ne 0) { Fail $LASTEXITCODE 'fleet set-zt не прошёл' }
    }
    Write-Host '-- шифрую паролем флота (тем же, что при init)'
    $bundle = (& $cli fleet encrypt --secrets $secrets | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $bundle) { Fail 1 'fleet encrypt не прошёл' }
    Set-RailwayVar $bundleVar $bundle
    Write-Host "-- готово: $bundleVar обновлён в сервисе $Service; на других машинах — .\tools\fleet.ps1 pull" -ForegroundColor Green
    exit 0
}

# pull
$vars = Get-RailwayVars
$bundle = [string]$vars.$bundleVar
if (-not $bundle) { Fail 3 "в Railway нет $bundleVar — флот ещё не создан: .\tools\fleet.ps1 init на машине с текущими секретами" }
$cli = Get-FleetCli

# Прежний ssh-ключ этой машины не теряем: им ещё могут открываться клиенты со старым пакетом.
$oldKey = Join-Path $secrets 'svc_diag_key'
if ((Test-Path $oldKey) -and -not (Test-Path (Join-Path $secrets 'fleet.json'))) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item $oldKey "$oldKey.local-$stamp" -Force
    if (Test-Path "$oldKey.pub") { Copy-Item "$oldKey.pub" "$oldKey.local-$stamp.pub" -Force }
    Write-Host "-- прежний ssh-ключ этой машины сохранён как svc_diag_key.local-$stamp"
}
$prevToken = Get-PrevAgentToken

$env:SZDIAG_FLEET_BUNDLE = $bundle
try {
    & $cli fleet decrypt --secrets $secrets
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:\SZDIAG_FLEET_BUNDLE -ErrorAction SilentlyContinue
}
if ($code -ne 0) { exit $code }

# Переход: апдейтеры, уже разданные с ЭТОЙ машины, несут её прежний токен — hub флота
# должен их принимать (ExtraAgentTokens собирается build-dist из secrets\agent-tokens.txt).
if ($prevToken) {
    $fleetToken = [string]((Get-Content (Join-Path $secrets 'fleet.json') -Raw | ConvertFrom-Json).agentToken)
    $tokensFile = Join-Path $secrets 'agent-tokens.txt'
    $known = if (Test-Path $tokensFile) { @(Get-Content $tokensFile | ForEach-Object { $_.Trim() }) } else { @() }
    if ($prevToken -ne $fleetToken -and $known -notcontains $prevToken) {
        Add-Content -Path $tokensFile -Value "# прежний токен этой машины до перехода на флот ($(Get-Date -Format 'yyyy-MM-dd'))"
        Add-Content -Path $tokensFile -Value $prevToken
        Write-Host '-- прежний токен этой машины добавлен в secrets\agent-tokens.txt (переходный)'
    }
}
Write-Host '-- готово: секреты флота в secrets\' -ForegroundColor Green
exit 0
} finally { Pop-Location }
