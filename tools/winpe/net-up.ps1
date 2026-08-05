<#
  Поднимает сеть в WinPE и, если сетевуха не опозналась, сама доставляет драйвер
  с флешки. Запускается из startnet.cmd до старта агента.

  Зачем: PE без драйвера NIC молча остаётся без адаптера, а агент падает
  стектрейсом `SocketException 10051 network unreachable` (живая СЗ 159948).
  Здесь — понятный статус и авто-подхват драйверов, без ручного drvload.

  ВНИМАНИЕ: весь вывод — латиницей. Консоль WinPE сидит на cp437 с растровым
  шрифтом, кириллица выводится как «?????» (см. комментарий в build-winpe.ps1).
  В PE нет модулей NetTCPIP/NetAdapter, поэтому только CIM + wpeutil/pnputil.
#>
param(
    # Папка с .inf на флешке (рекурсивно), подхватывается при промахе.
    [string]$DriversPath,
    # Хост hub для проверки достижимости (необязательно).
    [string]$HubHost,
    # Сколько ждать адрес от DHCP после каждой попытки.
    [int]$WaitSeconds = 20
)

$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"

if (-not $HubHost) {
    # Хост hub берём из конфига агента рядом со скриптом — чтобы не дублировать адрес.
    $cfg = Join-Path $PSScriptRoot "appsettings.json"
    if (Test-Path $cfg) {
        try {
            $url = (Get-Content $cfg -Raw | ConvertFrom-Json).HubUrl
            if ($url) { $HubHost = ([Uri]$url).Host }
        } catch { }
    }
}

function Get-Ipv4 {
    # Win32_* вместо Get-NetIPAddress: в PE модуля NetTCPIP нет.
    $cfgs = Get-CimInstance Win32_NetworkAdapterConfiguration -ErrorAction SilentlyContinue |
            Where-Object { $_.IPEnabled }
    foreach ($c in $cfgs) {
        foreach ($ip in @($c.IPAddress)) {
            if ($ip -match '^\d+\.\d+\.\d+\.\d+$' -and
                $ip -notmatch '^(127\.|169\.254\.|0\.0\.0\.0)') {
                return [pscustomobject]@{ Ip = $ip; Adapter = $c.Description; Gateway = @($c.DefaultIPGateway)[0] }
            }
        }
    }
    return $null
}

function Get-NetAdapters {
    Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.PNPDeviceID -and $_.PNPDeviceID -notmatch '^ROOT\\' }
}

function Get-MissingNetDevices {
    # Устройства без драйвера: класс ещё не определён, поэтому ловим по коду ошибки.
    Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.ConfigManagerErrorCode -ne 0 -and
                       $_.PNPDeviceID -match '^(PCI|USB)\\' }
}

function Wait-ForIp([int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) {
        $ip = Get-Ipv4
        if ($ip) { return $ip }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Initialize-Net {
    & wpeutil.exe InitializeNetwork 2>&1 | Out-Null
    # Второй вызов на уже поднятой сети безвреден и возвращает ошибку — глушим.
}

Write-Host ""
Write-Host "== network ==" -ForegroundColor Cyan

Initialize-Net
$ip = Wait-ForIp -seconds $WaitSeconds

if (-not $ip) {
    $adapters = @(Get-NetAdapters)
    Write-Host ("NIC adapters detected: {0}" -f $adapters.Count)

    if ($DriversPath -and (Test-Path $DriversPath)) {
        $missing = @(Get-MissingNetDevices)
        Write-Host ("Devices without driver: {0}" -f $missing.Count)

        # Прицельно: ищем .inf, где встречается hardware ID неопознанного
        # устройства, и грузим только его — массовый pnputil по всему паку
        # в PE занимает минуты.
        $loaded = 0
        foreach ($dev in $missing) {
            if ($dev.PNPDeviceID -notmatch '(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})|(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})') { continue }
            $hwid = $Matches[0]
            Write-Host ("Searching driver for {0} ..." -f $hwid)

            $hit = Get-ChildItem $DriversPath -Recurse -Filter *.inf -ErrorAction SilentlyContinue |
                   Select-String -Pattern $hwid -SimpleMatch -List -ErrorAction SilentlyContinue |
                   Select-Object -First 3
            foreach ($h in $hit) {
                Write-Host ("  drvload {0}" -f $h.Path)
                & drvload.exe $h.Path 2>&1 | Out-Null
                $loaded++
            }
            if (-not $hit) { Write-Host "  no match in driver pack" -ForegroundColor Yellow }
        }

        if ($loaded -eq 0) {
            # Ничего не подошло прицельно — заливаем пак целиком (долго, но
            # ловит случаи, когда устройство не отдало внятный hardware ID).
            Write-Host "Fallback: installing whole driver pack (may take a minute) ..."
            & pnputil.exe /add-driver (Join-Path $DriversPath "*.inf") /subdirs /install 2>&1 | Out-Null
        }

        Initialize-Net
        $ip = Wait-ForIp -seconds $WaitSeconds
    } else {
        Write-Host "Driver folder not found on USB: $DriversPath" -ForegroundColor Yellow
    }
}

if ($ip) {
    Write-Host ("IP      : {0}" -f $ip.Ip) -ForegroundColor Green
    Write-Host ("Adapter : {0}" -f $ip.Adapter)
    if ($ip.Gateway) { Write-Host ("Gateway : {0}" -f $ip.Gateway) }

    if ($HubHost) {
        $ok = Test-Connection -ComputerName $HubHost -Count 2 -Quiet -ErrorAction SilentlyContinue
        if ($ok) {
            Write-Host ("Hub {0} : reachable" -f $HubHost) -ForegroundColor Green
        } else {
            Write-Host ("Hub {0} : NO PING (wrong subnet/VLAN or hub is down)" -f $HubHost) -ForegroundColor Yellow
        }
    }
    exit 0
}

Write-Host "NO NETWORK." -ForegroundColor Red
Write-Host "Options:"
Write-Host "  1) plug a USB-Ethernet dongle, then run:  net-up"
Write-Host "  2) copy the .inf of this NIC to <usb>\drivers\ and run:  net-up"
Write-Host "  3) static IP:"
Write-Host '     netsh interface ip show interfaces'
# Одинарные кавычки: в PowerShell \" не экранирует, а < внутри двойных кавычек
# парсер видит как оператор перенаправления — скрипт падал целиком (СЗ 159948).
Write-Host '     netsh interface ip set address name="<idx>" static <ip> <mask> <gw>'
exit 1
