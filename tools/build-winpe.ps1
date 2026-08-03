<#
.SYNOPSIS
  Собирает загрузочную WinPE-флешку с агентом sz-diag на борту.

  Зачем: диагностика машины, которая не грузится (или заражена) — агент стартует
  в чистом PE, коннектится к hub исходящим SignalR и обслуживает `szcli exec`/`diag`.
  sshd в PE не нужен и невозможен (нет SAM/учёток) — работаем через exec.

  Образ PE строится с нуля из ADK (установочную флешку винды не трогаем: в её
  boot.wim нет PowerShell, а без него агент бесполезен).

.DESCRIPTION
  Агент кладётся НА ФЛЕШКУ (папка szdiag\), а не внутрь boot.wim — обновить агента
  потом можно простым копированием файла, без пересборки образа.

.PARAMETER UsbDrive
  Буква флешки, напр. G:. Внимание: диск будет ПОЛНОСТЬЮ отформатирован (FAT32).

.PARAMETER Work
  Рабочая папка сборки (создаётся заново). По умолчанию C:\winpe-szdiag.

.PARAMETER DriverPath
  Папка с драйверами сетевых карт (.inf) для инжекта в образ. Без драйверов свежие
  2.5G Realtek/Intel в PE не поднимутся, и агент не достучится до hub.

.PARAMETER ScratchMb
  Размер RAM-диска под запись внутри PE (32/64/128/256/512/1024). По умолчанию 1024:
  меньше требует место single-file агент под распаковку, а `DISM /Cleanup-Image` прямо
  ругается «recommended size is at least 1024 MB» (поймано на живой СЗ 160450).

.PARAMETER SkipMedia
  Только собрать boot.wim, флешку не трогать (для отладки образа).

.EXAMPLE
  # от АДМИНА:
  .\tools\build-winpe.ps1 -UsbDrive G:
  .\tools\build-winpe.ps1 -UsbDrive G: -DriverPath C:\drivers\nic
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$UsbDrive,
    [string]$Work = "C:\winpe-szdiag",
    [string]$DriverPath = "",
    [ValidateSet(32, 64, 128, 256, 512, 1024)]
    [int]$ScratchMb = 1024,
    [switch]$SkipMedia,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "== sz-diag: сборка WinPE-флешки =="

# --- 0. Предполётные проверки -------------------------------------------------

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Нужны права администратора (copype/Dism/MakeWinPEMedia без них не работают). " +
          "Запусти PowerShell от админа и повтори."
}

$adk = "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit"
$peRoot = Join-Path $adk "Windows Preinstallation Environment"
$ocs = Join-Path $peRoot "amd64\WinPE_OCs"
$dandi = Join-Path $adk "Deployment Tools\DandISetEnv.bat"

foreach ($p in @($peRoot, $ocs, $dandi)) {
    if (-not (Test-Path $p)) { throw "Не найдено: $p. Установлен ли ADK + WinPE add-on?" }
}

$clientDist = Join-Path $root "dist\client"
if (-not (Test-Path (Join-Path $clientDist "SzDiag.Agent.exe"))) {
    throw "Нет dist\client\SzDiag.Agent.exe — сначала прогони .\tools\build-dist.ps1"
}

$UsbDrive = $UsbDrive.TrimEnd('\')
if ($UsbDrive -notmatch '^[A-Za-z]:$') { throw "UsbDrive должен быть вида G: (получено '$UsbDrive')" }
$usbLetter = $UsbDrive.Substring(0, 1)

if (-not $SkipMedia) {
    $vol = Get-Volume -DriveLetter $usbLetter -ErrorAction SilentlyContinue
    if (-not $vol) { throw "Том $UsbDrive не найден." }

    $disk = Get-Partition -DriveLetter $usbLetter | Get-Disk
    if ($disk.BusType -ne 'USB') {
        throw "Диск $UsbDrive имеет BusType=$($disk.BusType), а не USB. " +
              "Отказываюсь форматировать — проверь букву."
    }

    Write-Host ""
    Write-Host "  ЦЕЛЬ: $UsbDrive  диск #$($disk.Number)  $($disk.FriendlyName)  " -NoNewline
    Write-Host "$([math]::Round($disk.Size/1GB,1)) ГБ" -ForegroundColor Yellow
    Write-Host "  Диск будет ПОЛНОСТЬЮ ОТФОРМАТИРОВАН (FAT32), все данные пропадут." -ForegroundColor Yellow
    Write-Host ""
    if (-not $Force) {
        $answer = Read-Host "  Продолжить? (введи YES)"
        if ($answer -ne "YES") { Write-Host "Отменено."; exit 1 }
    }
}

# --- 1. Чистый образ PE из ADK ------------------------------------------------

if (Test-Path $Work) {
    Write-Host "-- чищу рабочую папку $Work"
    # Хвост от прерванной сборки: примонтированный образ иначе держит папку.
    & dism.exe /English /Cleanup-Wim | Out-Null
    Remove-Item $Work -Recurse -Force
}

Write-Host "-- copype amd64 -> $Work"
$copype = "call `"$dandi`" && copype amd64 `"$Work`""
& cmd.exe /c $copype
if ($LASTEXITCODE -ne 0) { throw "copype упал (код $LASTEXITCODE)" }

$bootWim = Join-Path $Work "media\sources\boot.wim"
$mount = Join-Path $Work "mount"
if (-not (Test-Path $bootWim)) { throw "copype не создал $bootWim" }

Write-Host "-- монтирую boot.wim -> $mount"
& dism.exe /English /Mount-Image /ImageFile:"$bootWim" /Index:1 /MountDir:"$mount"
if ($LASTEXITCODE -ne 0) { throw "Mount-Image упал (код $LASTEXITCODE)" }

$mounted = $true
try {
    # --- 2. Опциональные компоненты -------------------------------------------
    # Порядок важен: PowerShell зависит от WMI + NetFx + Scripting. Для каждого
    # пакета ставим ещё его en-us пару, иначе командлеты без ресурсов.
    $packages = @(
        "WinPE-WMI",             # Get-CimInstance — вся диагностика железа
        "WinPE-NetFx",           # .NET-зависимости, нужен для PowerShell
        "WinPE-Scripting",       # WSH
        "WinPE-PowerShell",      # собственно PowerShell
        "WinPE-StorageWMI",      # Get-Disk/Get-PhysicalDisk, SMART
        "WinPE-DismCmdlets",     # работа с offline-образом винды клиента
        "WinPE-EnhancedStorage", # распознавание дисков/BitLocker-томов
        "WinPE-SecureStartup"    # BitLocker: разблокировать диск для offline-разбора
    )

    foreach ($pkg in $packages) {
        $cab = Join-Path $ocs "$pkg.cab"
        $lang = Join-Path $ocs "en-us\${pkg}_en-us.cab"
        if (-not (Test-Path $cab)) { throw "Нет пакета $cab" }

        Write-Host "-- пакет $pkg"
        & dism.exe /English /Image:"$mount" /Add-Package /PackagePath:"$cab"
        if ($LASTEXITCODE -ne 0) { throw "Add-Package $pkg упал (код $LASTEXITCODE)" }

        if (Test-Path $lang) {
            & dism.exe /English /Image:"$mount" /Add-Package /PackagePath:"$lang"
            if ($LASTEXITCODE -ne 0) { throw "Add-Package ${pkg}_en-us упал (код $LASTEXITCODE)" }
        }
    }

    # --- 3. Драйверы сетевых карт ---------------------------------------------
    if ($DriverPath) {
        if (-not (Test-Path $DriverPath)) { throw "DriverPath не найден: $DriverPath" }
        Write-Host "-- инжектю драйверы из $DriverPath"
        & dism.exe /English /Image:"$mount" /Add-Driver /Driver:"$DriverPath" /Recurse /ForceUnsigned
        if ($LASTEXITCODE -ne 0) { throw "Add-Driver упал (код $LASTEXITCODE)" }
    } else {
        Write-Host "-- драйверы NIC не заданы (-DriverPath): если сетевуха не поднимется," -ForegroundColor Yellow
        Write-Host "   агент не найдёт hub. Тогда пересобрать с драйверами." -ForegroundColor Yellow
    }

    # --- 4. Место под запись внутри PE ----------------------------------------
    Write-Host "-- scratch space = $ScratchMb МБ"
    & dism.exe /English /Image:"$mount" /Set-ScratchSpace:$ScratchMb
    if ($LASTEXITCODE -ne 0) { throw "Set-ScratchSpace упал (код $LASTEXITCODE)" }

    # --- 5. Автозапуск --------------------------------------------------------
    # Флешка в PE получает произвольную букву, поэтому ищем её по маркеру.
    # Агент лежит на флешке, а не в образе — чтобы обновлять без пересборки.
    #
    # ВНИМАНИЕ: текст в .cmd — только латиница. Консоль WinPE сидит на cp437 с
    # растровым шрифтом, кириллицы в ней нет ни в какой кодировке — вместо неё
    # выводятся «?????». Русский вывод остаётся в самом агенте (.NET, UTF-8).
    $startnet = @'
@echo off
wpeinit
echo.
echo == sz-diag WinPE ==
echo.

set SZUSB=
for %%d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist %%d:\szdiag\SzDiag.Agent.exe set SZUSB=%%d:
)

if "%SZUSB%"=="" (
    echo [!] SzDiag.Agent.exe not found on any drive.
    echo     Is the USB stick mounted? Check: diskpart ^> list volume
    goto :shell
)

echo Agent found: %SZUSB%\szdiag
echo Network: initialized via wpeinit.
echo.
echo To start the agent, type:  szdiag
echo.

rem Single-file .NET распаковывается во временную папку. В RAM-диске места мало,
rem поэтому уводим распаковку на флешку.
set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%SZUSB%\szdiag\bundle
doskey szdiag=%SZUSB%\szdiag\szdiag.cmd $*

:shell
'@
    Set-Content -Path (Join-Path $mount "Windows\System32\startnet.cmd") `
                -Value $startnet -Encoding ascii

    Write-Host "-- размонтирую образ (коммит)"
    & dism.exe /English /Unmount-Image /MountDir:"$mount" /Commit
    if ($LASTEXITCODE -ne 0) { throw "Unmount-Image упал (код $LASTEXITCODE)" }
    $mounted = $false
} finally {
    if ($mounted) {
        Write-Host "!! ошибка — откатываю монтирование" -ForegroundColor Red
        & dism.exe /English /Unmount-Image /MountDir:"$mount" /Discard 2>&1 | Out-Null
    }
}

# --- 6. Запись на флешку ------------------------------------------------------

if ($SkipMedia) {
    Write-Host ""
    Write-Host "== Готово (только образ) =="
    Write-Host "boot.wim: $bootWim"
    return
}

Write-Host "-- пишу WinPE на $UsbDrive (форматирование FAT32)"
$make = "call `"$dandi`" && MakeWinPEMedia /UFD /f `"$Work`" $UsbDrive"
& cmd.exe /c $make
if ($LASTEXITCODE -ne 0) { throw "MakeWinPEMedia упал (код $LASTEXITCODE)" }

# --- 7. Агент на флешку -------------------------------------------------------

$szdir = Join-Path "$UsbDrive\" "szdiag"
Write-Host "-- копирую агента -> $szdir"
New-Item -ItemType Directory $szdir -Force | Out-Null
Copy-Item (Join-Path $clientDist "SzDiag.Agent.exe") $szdir -Force
Copy-Item (Join-Path $clientDist "appsettings.json") $szdir -Force
foreach ($opt in @("service_key.pub", "testsuite.json", "version.txt")) {
    $src = Join-Path $clientDist $opt
    if (Test-Path $src) { Copy-Item $src $szdir -Force }
}

# Лаунчер: спрашивает номер СЗ и поднимает агента в PE-режиме.
# Текст латиницей — см. комментарий про cp437 у $startnet.
$launcher = @'
@echo off
setlocal
set HERE=%~dp0
set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%HERE%bundle

set SZ=%1
if "%SZ%"=="" set /p SZ=SZ number:
if "%SZ%"=="" (
    echo No SZ number - agent will not start.
    exit /b 1
)

echo Starting agent in WinPE mode, SZ=%SZ%
"%HERE%SzDiag.Agent.exe" --pe %SZ%
'@
Set-Content -Path (Join-Path $szdir "szdiag.cmd") -Value $launcher -Encoding ascii

Write-Host ""
Write-Host "== Готово =="
Write-Host "Флешка:  $UsbDrive (загрузочная, UEFI + Legacy)"
Write-Host "Агент:   $szdir  (обновляется копированием, без пересборки образа)"
Write-Host "Hub:     $(if (Test-Path (Join-Path $szdir 'appsettings.json')) { (Get-Content (Join-Path $szdir 'appsettings.json') -Raw | ConvertFrom-Json).HubUrl })"
Write-Host ""
Write-Host "В PE: команда 'szdiag' (или szdiag <СЗ>) — поднимет агента."
