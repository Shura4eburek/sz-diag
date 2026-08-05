#Requires -Version 5.1
<#
.SYNOPSIS
  Работа с расписаниями OCCT: показать состав готового файла и собрать профили из эталона.

.DESCRIPTION
  Боль (бэклог п.10, СЗ 160306): расписание оказалось КОНЕЧНЫМ (`Combined 5 мин` →
  `PowerSupply 5 мин`, оба `IsInfinite=False`), тест сам завершился через 10:11 — а по
  внешнему виду это читалось как «машина держит нагрузку». Формат вендор не документирует,
  и глазами в 77 КБ JSON состав не разглядеть.

  `-Info` печатает состав любого расписания одной строкой на период: тип теста,
  длительность, бесконечность, режим CPU — и ИТОГО «тест закончится через N» либо
  «бесконечный».

  `-Make` собирает из эталона (`deploy\occt\schedule.json`) три профиля:
    schedule-smoke.json    — по 5 мин на период (дымовой прогон, ~10 мин всего)
    schedule-long.json     — по 1.5 ч на период (~3 ч, ночной/долгий прогон)
    schedule-infinite.json — IsInfinite на всех периодах (до вырубона или ручной остановки)
  Все настройки тестов (Extreme/AVX-512/потоки) берутся из эталона как есть — меняется
  только длительность.

.EXAMPLE
  .\tools\occt-schedule.ps1 -Info deploy\occt\schedule.json
  .\tools\occt-schedule.ps1 -Make
#>
param(
    [string]$Info,
    [switch]$Make,
    [string]$Source = "deploy\occt\schedule.json",
    [string]$OutDir = "deploy\occt"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Show-Schedule([string]$path) {
    if (-not (Test-Path $path)) { throw "Расписание не найдено: $path" }
    $j = Get-Content $path -Raw | ConvertFrom-Json
    Write-Host "== $path ==" -ForegroundColor Cyan
    Write-Host "ScheduleType: $($j.ScheduleType); периодов: $($j.Periods.Count)"

    $total = [TimeSpan]::Zero
    $infinite = $false
    foreach ($p in $j.Periods) {
        $mode = $p.CpuOcctConfig.Mode
        $iset = $p.CpuOcctConfig.OcctInstructionSet
        if ($p.IsInfinite) {
            $infinite = $true
            Write-Host ("  {0,-12} БЕСКОНЕЧНО      cpu: {1}/{2}" -f $p.TestType, $mode, $iset)
        } else {
            $total += [TimeSpan]::Parse($p.Duration)
            Write-Host ("  {0,-12} {1,-15} cpu: {2}/{3}" -f $p.TestType, $p.Duration, $mode, $iset)
        }
    }

    if ($infinite) {
        Write-Host "ИТОГО: расписание бесконечное — тест идёт до вырубона или ручной остановки." -ForegroundColor Green
    } else {
        Write-Host ("ИТОГО: тест САМ завершится через {0:hh\:mm\:ss}. Если машина 'выстояла' дольше — это не заслуга теста." -f $total) -ForegroundColor Yellow
    }
}

function New-Profile([object]$template, [string]$path, [scriptblock]$tweak) {
    # Клонируем через JSON: ConvertFrom-Json отдаёт связанные объекты, править эталон нельзя.
    $copy = ($template | ConvertTo-Json -Depth 100) | ConvertFrom-Json
    foreach ($p in $copy.Periods) { & $tweak $p }
    ($copy | ConvertTo-Json -Depth 100) | Set-Content -Path $path -Encoding utf8
    Write-Host "-- собрано: $path"
    Show-Schedule $path
}

if ($Info) { Show-Schedule $Info; return }

if (-not $Make) {
    Write-Host "Использование:"
    Write-Host "  .\tools\occt-schedule.ps1 -Info <schedule.json>   показать состав и длительность"
    Write-Host "  .\tools\occt-schedule.ps1 -Make                   собрать smoke/long/infinite из эталона"
    return
}

if (-not (Test-Path $Source)) { throw "Эталон не найден: $Source" }
$template = Get-Content $Source -Raw | ConvertFrom-Json

New-Profile $template (Join-Path $OutDir "schedule-smoke.json")    { param($p) $p.Duration = "00:05:00"; $p.IsInfinite = $false }
New-Profile $template (Join-Path $OutDir "schedule-long.json")     { param($p) $p.Duration = "01:30:00"; $p.IsInfinite = $false }
New-Profile $template (Join-Path $OutDir "schedule-infinite.json") { param($p) $p.IsInfinite = $true }
