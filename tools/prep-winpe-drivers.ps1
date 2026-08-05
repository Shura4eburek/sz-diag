<#
.SYNOPSIS
  Готовит пак сетевых драйверов для WinPE из локального SDI (Snappy Driver Installer).

  Зачем: PE без драйвера NIC бесполезен — агент не достучится до hub
  (`SocketException 10051 network unreachable`, поймано на живой СЗ 159948).
  Индивидуально подбирать .inf под каждую машину — не вариант, поэтому
  собираем универсальный пак один раз и зашиваем в образ/флешку.

.DESCRIPTION
  Из SDI-паков `DP_LAN_*` берём только x64-ветки под Win10/11 (в SDI они лежат
  в сегментах пути вида `10x64`, `11x64`, `88110x64`, `NTx64`) — остальное
  (x86, XP/Vista/7) в PE не нужно и только раздувает образ.

  Результат делится на два слоя:
    core\  — Intel + Realtek, инжектится в boot.wim (грузится сразу, ~50-80 МБ)
    extra\ — всё остальное (Others: Broadcom/Killer/Aquantia/USB-донглы),
             кладётся на флешку и подхватывается в PE через pnputil только
             если сеть не поднялась. Так boot.wim остаётся лёгким, а редкая
             сетевуха добавляется копированием на флешку без пересборки.

.PARAMETER SdiPath
  Папка SDI (внутри неё drivers\DP_LAN_*.7z).

.PARAMETER OutPath
  Куда сложить результат. Внутри создаются core\ и extra\.

.EXAMPLE
  .\tools\prep-winpe-drivers.ps1
  .\tools\build-winpe.ps1 -UsbDrive G:      # подхватит пак автоматически
#>
param(
    [string]$SdiPath = "C:\Share\SDI",
    [string]$OutPath = "C:\winpe-szdiag-drivers",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Сегменты пути SDI, которые годятся для PE (amd64, Win10/11).
# 88110x64 = пак, покрывающий 8/8.1/10; NTx64 = универсальный NT-драйвер.
$okSegments = @('10x64', '11x64', '88110x64', 'ntx64')

# Ветки, которые в сервисном центре не встретятся и только жрут место в образе:
# 40G/100G-серверные адаптеры, SR-IOV virtual functions и утилита Intel PROSet
# (последняя вообще не драйвер). Экономит ~32 МБ в boot.wim.
$skipSegments = @('pro40gb', 'procgb', 'proavf', 'prosetdx')

# Какой пак в какой слой. Имя — префикс архива без версии.
$layers = @{
    core  = @('DP_LAN_Intel', 'DP_LAN_Realtek-NT')
    extra = @('DP_LAN_Others')
}

Write-Host "== sz-diag: пак сетевых драйверов для WinPE =="

# --- 0. Распаковщик -----------------------------------------------------------

function Get-Extractor {
    $sevenZip = @(
        "$env:ProgramFiles\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($sevenZip) { return @{ Exe = $sevenZip; Kind = '7z' } }

    $winrar = @(
        "$env:ProgramFiles\WinRAR\WinRAR.exe",
        "${env:ProgramFiles(x86)}\WinRAR\WinRAR.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($winrar) { return @{ Exe = $winrar; Kind = 'winrar' } }

    throw "Не найден распаковщик .7z (7-Zip или WinRAR). Поставь 7-Zip."
}

function Expand-Pack($extractor, [string]$archive, [string]$dest) {
    New-Item -ItemType Directory $dest -Force | Out-Null
    if ($extractor.Kind -eq '7z') {
        & $extractor.Exe x $archive "-o$dest" -y -bso0 -bsp0 | Out-Null
    } else {
        # WinRAR: -ibck = без окна, путь назначения обязан кончаться слешем.
        & $extractor.Exe x -y -ibck $archive "$dest\" | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Распаковка упала (код $LASTEXITCODE): $archive" }
}

$extractor = Get-Extractor
Write-Host "-- распаковщик: $($extractor.Exe)"

# --- 1. Поиск паков -----------------------------------------------------------

$driversDir = Join-Path $SdiPath "drivers"
if (-not (Test-Path $driversDir)) { throw "Не найдено: $driversDir (это точно папка SDI?)" }

if (Test-Path $OutPath) {
    if (-not $Force) {
        Write-Host "-- $OutPath уже есть, пересобираю (старое содержимое удаляется)"
    }
    Remove-Item $OutPath -Recurse -Force
}
New-Item -ItemType Directory $OutPath -Force | Out-Null

$tmp = Join-Path $env:TEMP "szdiag-nic-$(Get-Random)"
New-Item -ItemType Directory $tmp -Force | Out-Null

try {
    foreach ($layer in @('core', 'extra')) {
        $layerDir = Join-Path $OutPath $layer
        New-Item -ItemType Directory $layerDir -Force | Out-Null

        foreach ($prefix in $layers[$layer]) {
            # Версия в имени архива меняется при обновлении SDI — берём свежий.
            $archive = Get-ChildItem $driversDir -Filter "$prefix`_*.7z" |
                       Sort-Object Name -Descending | Select-Object -First 1
            if (-not $archive) {
                Write-Host "-- [!] нет пака $prefix* в $driversDir — пропускаю" -ForegroundColor Yellow
                continue
            }

            Write-Host "-- распаковываю $($archive.Name) ($([math]::Round($archive.Length/1MB)) МБ)"
            $unpacked = Join-Path $tmp $prefix
            Expand-Pack $extractor $archive.FullName $unpacked

            # Оставляем только x64-ветки под Win10/11: у SDI это отдельный
            # сегмент пути, поэтому режем по сегментам, а не по маске имени.
            $copied = 0
            Get-ChildItem $unpacked -Recurse -Directory | ForEach-Object {
                if ($okSegments -notcontains $_.Name.ToLowerInvariant()) { return }
                # .inf может лежать глубже (10x64\PRO1000\...), поэтому копируем ветку целиком.
                $rel = $_.FullName.Substring($unpacked.Length).TrimStart('\')
                $dst = Join-Path (Join-Path $layerDir $prefix) $rel
                if (Test-Path $dst) { return }   # вложенная ветка уже уехала с родителем
                New-Item -ItemType Directory (Split-Path $dst -Parent) -Force | Out-Null
                Copy-Item $_.FullName $dst -Recurse -Force
                $copied++
            }

            $infs = @(Get-ChildItem (Join-Path $layerDir $prefix) -Recurse -Filter *.inf -ErrorAction SilentlyContinue)
            $mb = [math]::Round((Get-ChildItem (Join-Path $layerDir $prefix) -Recurse -File |
                                 Measure-Object Length -Sum).Sum / 1MB, 1)
            Write-Host "   $prefix -> $layer : веток $copied, .inf $($infs.Count), $mb МБ"

            Remove-Item $unpacked -Recurse -Force
        }
    }
} finally {
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

# --- 2. Чистка ненужных веток -------------------------------------------------
# Режем после копирования: ветки копируются целиком, а skip-папки сидят внутри них.
do {
    $junk = @(Get-ChildItem $OutPath -Recurse -Directory -ErrorAction SilentlyContinue |
              Where-Object { $skipSegments -contains $_.Name.ToLowerInvariant() })
    foreach ($d in $junk) {
        if (Test-Path $d.FullName) { Remove-Item $d.FullName -Recurse -Force }
    }
} while ($junk.Count -gt 0)

# --- 3. Итог ------------------------------------------------------------------

foreach ($layer in @('core', 'extra')) {
    $dir = Join-Path $OutPath $layer
    $files = @(Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)
    $mb = if ($files) { [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1) } else { 0 }
    $infs = @($files | Where-Object Extension -eq '.inf').Count
    Write-Host ""
    Write-Host "$layer : $infs .inf, $mb МБ  ($dir)"
}

Write-Host ""
Write-Host "== Готово =="
Write-Host "core  -> инжектится в boot.wim"
Write-Host "extra -> едет на флешку, подхват в PE при промахе"
Write-Host "Дальше: .\tools\build-winpe.ps1 -UsbDrive <буква>"
