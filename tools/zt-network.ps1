<#
.SYNOPSIS
  Сервисная ZT-сеть на своём контроллере (T037, specs/002-domain-independent-access).

.DESCRIPTION
  Через прокси контроллера (сервис szdiag-zt-controller на своём Railway) создаёт или
  приводит к нужному виду сервисную сеть и подключает к ней ЭТУ машину-hub со статическим
  адресом. Повторный запуск безопасен: сеть та же, конфиг перезаписывается.

    .\tools\zt-network.ps1 -ControllerUrl https://<свой>.up.railway.app      # ПК → 10.88.0.1
    .\tools\zt-network.ps1 -HubAddress 10.88.0.2                             # ноут (URL/токен — из флота)

  Сеть публичная (клиент входит без ручного authorize — ID сети лежит в указателе), поэтому
  ограничения держат flow rules: клиенты видят только адреса hub-машин, клиент↔клиент — drop.
  Порты hub правила НЕ режут (stateless-правилам нечем отличить ответ ssh от нового
  соединения) — входящее на hub-машину ограничивают Windows Firewall (профиль сети ZT) и
  LocalAddressFilter hub (T048).

  Итог — secrets\zt.json (url и токен прокси, бэкап identity контроллера, id сети). Если флот
  уже создан — дальше .\tools\fleet.ps1 push; если нет — fleet.ps1 init подхватит его сам.

  Админ не нужен: локальная служба ZeroTier One принимает пользовательский токен
  (%LOCALAPPDATA%\ZeroTier\authtoken.secret).

  Коды: 0 ок · 2 нет URL/токена контроллера · 3 контроллер не отвечает · 4 нет ZeroTier One ·
        5 адрес hub не назначился · 1 прочее
#>
param(
    [string]$ControllerUrl,
    [string]$Network,
    [string]$HubAddress = '10.88.0.1',
    # Адреса всех hub-машин флота — только к ним клиентам разрешено ходить.
    [string[]]$HubAddresses = @('10.88.0.1', '10.88.0.2'),
    [string]$Subnet = '10.88.0.0/24',
    [string]$PoolStart = '10.88.0.10',
    [string]$PoolEnd = '10.88.0.250',
    # Только сеть на контроллере, без подключения этой машины.
    [switch]$NoJoin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$secrets = Join-Path $root 'secrets'
$ztJsonPath = Join-Path $secrets 'zt.json'

function Fail([int]$code, [string]$msg) {
    Write-Host "zt-network: $msg" -ForegroundColor Red
    exit $code
}

function Read-JsonOrNull([string]$path) {
    if (Test-Path $path) { return (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    return $null
}

# --- откуда брать URL и токен прокси: параметр → zt.json → fleet.json → secrets\zt-controller.token
$zt = Read-JsonOrNull $ztJsonPath
$fleet = Read-JsonOrNull (Join-Path $secrets 'fleet.json')
if (-not $ControllerUrl) { $ControllerUrl = @($zt.controllerUrl, $fleet.ztControllerUrl) | Where-Object { $_ } | Select-Object -First 1 }
$token = @($zt.controllerToken, $fleet.ztControllerToken) | Where-Object { $_ } | Select-Object -First 1
$tokenFile = Join-Path $secrets 'zt-controller.token'
if (-not $token -and (Test-Path $tokenFile)) { $token = (Get-Content $tokenFile -Raw).Trim() }
if (-not $Network) { $Network = @($zt.network, $fleet.ztNetwork) | Where-Object { $_ } | Select-Object -First 1 }
if (-not $ControllerUrl) { Fail 2 'не задан -ControllerUrl (адрес сервиса szdiag-zt-controller на своём Railway)' }
if (-not $token) { Fail 2 "нет токена прокси: ни во флоте, ни в $tokenFile" }
$ControllerUrl = $ControllerUrl.TrimEnd('/')

function Invoke-Ctl([string]$method, [string]$path, $body = $null) {
    $p = @{ Method = $method; Uri = "$ControllerUrl$path"; Headers = @{ Authorization = "Bearer $token" }; TimeoutSec = 20 }
    if ($null -ne $body) { $p.Body = ($body | ConvertTo-Json -Depth 10 -Compress); $p.ContentType = 'application/json' }
    return Invoke-RestMethod @p
}

try { $status = Invoke-Ctl GET '/status' }
catch { Fail 3 "контроллер $ControllerUrl не отвечает: $($_.Exception.Message)" }
$ctl = [string]$status.address
Write-Host "-- контроллер $ctl ($ControllerUrl), online=$($status.online)"

# --- flow rules: ARP и IPv4; к hub-машинам и от них — можно, остальное — drop.
$rules = @(
    @{ type = 'MATCH_ETHERTYPE'; not = $true; or = $false; etherType = 2048 },
    @{ type = 'MATCH_ETHERTYPE'; not = $true; or = $false; etherType = 2054 },
    @{ type = 'ACTION_DROP' },
    @{ type = 'MATCH_ETHERTYPE'; not = $false; or = $false; etherType = 2054 },
    @{ type = 'ACTION_ACCEPT' }
)
foreach ($h in $HubAddresses) {
    $rules += @{ type = 'MATCH_IPV4_SOURCE'; not = $false; or = $false; ip = "$h/32" }, @{ type = 'ACTION_ACCEPT' }
    $rules += @{ type = 'MATCH_IPV4_DEST'; not = $false; or = $false; ip = "$h/32" }, @{ type = 'ACTION_ACCEPT' }
}
$rules += @{ type = 'ACTION_DROP' }

$config = @{
    name = 'szdiag-service'
    private = $false
    enableBroadcast = $true
    multicastLimit = 32
    v4AssignMode = @{ zt = $true }
    v6AssignMode = @{ zt = $false; '6plane' = $false; rfc4193 = $false }
    ipAssignmentPools = @(@{ ipRangeStart = $PoolStart; ipRangeEnd = $PoolEnd })
    routes = @(@{ target = $Subnet })
    rules = $rules
}

if ($Network) {
    if (-not $Network.StartsWith($ctl)) {
        Fail 1 "сеть $Network принадлежит другому контроллеру (адрес контроллера $ctl). Потерян volume без бэкапа identity? Тогда сеть новая: удали network из secrets\zt.json и запусти снова"
    }
    $net = Invoke-Ctl POST "/controller/network/$Network" $config
    Write-Host "-- сеть ${Network}: конфиг обновлён"
} else {
    $net = Invoke-Ctl POST "/controller/network/${ctl}______" $config
    $Network = [string]$net.id
    Write-Host "-- сеть создана: $Network" -ForegroundColor Green
}
Write-Host "   правил: $(@($net.rules).Count), пул $PoolStart–$PoolEnd, маршрут $Subnet"

$identity = (Invoke-Ctl GET '/admin/identity')
if ($identity -isnot [string]) { $identity = [string]$identity }
$identity = $identity.Trim()

# --- zt.json сразу: даже если join ниже упадёт, сеть и identity не потеряются.
New-Item -ItemType Directory $secrets -Force | Out-Null
$out = [ordered]@{ controllerUrl = $ControllerUrl; controllerToken = $token; identitySecret = $identity; network = $Network }
[IO.File]::WriteAllText($ztJsonPath, ($out | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
Write-Host "-- записано: secrets\zt.json (с бэкапом identity контроллера)"

if (-not $NoJoin) {
    $authFile = Join-Path $env:LOCALAPPDATA 'ZeroTier\authtoken.secret'
    if (-not (Test-Path $authFile)) { Fail 4 "нет $authFile — ZeroTier One не установлен на этой машине" }
    $localAuth = (Get-Content $authFile -Raw).Trim()
    $portFile = Join-Path $env:ProgramData 'ZeroTier\One\zerotier-one.port'
    $port = if (Test-Path $portFile) { (Get-Content $portFile -Raw).Trim() } else { '9993' }
    $local = "http://127.0.0.1:$port"
    $lh = @{ 'X-ZT1-Auth' = $localAuth }

    $me = Invoke-RestMethod "$local/status" -Headers $lh -TimeoutSec 10
    $node = [string]$me.address
    Write-Host "-- эта машина: узел $node → $HubAddress"

    $null = Invoke-RestMethod -Method POST "$local/network/$Network" -Headers $lh -Body '{}' -ContentType 'application/json' -TimeoutSec 10
    $member = @{ authorized = $true; ipAssignments = @($HubAddress); noAutoAssignIps = $true; name = "hub-$env:COMPUTERNAME" }
    $null = Invoke-Ctl POST "/controller/network/$Network/member/$node" $member

    $ok = $false
    for ($i = 0; $i -lt 60; $i++) {
        $n = Invoke-RestMethod "$local/network/$Network" -Headers $lh -TimeoutSec 10
        if ($n.status -eq 'OK' -and (@($n.assignedAddresses) -match "^$([regex]::Escape($HubAddress))/")) { $ok = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ok) { Fail 5 "адрес $HubAddress не назначился (status=$($n.status), адреса: $(@($n.assignedAddresses) -join ', '))" }
    Write-Host "-- подключено: $($n.portDeviceName) $(@($n.assignedAddresses) -join ', ')" -ForegroundColor Green

    # -eq, а не -InterfaceAlias: квадратные скобки в имени адаптера параметр читает как wildcard
    # и не находит его никогда (так профиль Private неделями оставался бы незамеченным).
    $alias = "ZeroTier One [$Network]"
    $prof = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object InterfaceAlias -eq $alias
    if ($prof -and $prof.NetworkCategory -ne 'Public') {
        # Private открывает входящее для правил частной сети (SMB, RDP…), а участником публичной
        # сервисной сети может стать кто угодно — flow rules порты hub-машины не режут.
        Write-Host "   ВНИМАНИЕ: профиль Windows сервисной сети — $($prof.NetworkCategory), нужен Public:" -ForegroundColor Red
        Write-Host "   Get-NetConnectionProfile | ? InterfaceAlias -eq '$alias' | Set-NetConnectionProfile -NetworkCategory Public   (под админом)" -ForegroundColor Red
    } elseif ($prof) {
        Write-Host "   профиль Windows: Public (входящее закрыто по умолчанию)" -ForegroundColor Green
    }
}

if ($fleet) { Write-Host '-- дальше: .\tools\fleet.ps1 push — параметры сети уедут во флот' }
else { Write-Host '-- флот ещё не создан: .\tools\fleet.ps1 init подхватит secrets\zt.json сам' }
exit 0
