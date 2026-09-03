<#
.SYNOPSIS
  Проверка сервисного бокса перед выездом: не устарел ли пакет агента, есть ли что раздавать,
  на месте ли ключ.

.DESCRIPTION
  Боль (бэклог п.4): механику «агент переживает ребут» сделали 24.07, а на клиенте 160306 стоял
  агент от 23.07 — `build-dist.ps1` после фичи ни разу не гоняли, и она не доехала НИ ДО ОДНОГО
  клиента. Обнаружилось случайно, посреди диагностики. Апдейтер честно качал с hub то, что там
  лежало, — а лежала старая сборка.

  Скрипт сравнивает пакет агента (`dist\host\hub\agent-dist`) с последним коммитом, который
  трогал код агента, и ругается, если пакет отстал.

.EXAMPLE
  pwsh -File tools\doctor.ps1
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = "Stop"
$problems = @()

function Ok($text)   { Write-Host "  OK   $text" -ForegroundColor Green }
function Bad($text)  { Write-Host "  ЖДЁТ $text" -ForegroundColor Red; $script:problems += $text }
function Info($text) { Write-Host "  --   $text" -ForegroundColor DarkGray }

Write-Host "== sz-diag doctor ==" -ForegroundColor Cyan

# 1. Пакет агента: не отстал ли он от кода
$agentDist = Join-Path $Root "dist\host\hub\agent-dist"
$package = Join-Path $agentDist "package.zip"
if (-not (Test-Path $package)) {
    Bad "пакета агента нет ($package) — прогони .\tools\build-dist.ps1"
} else {
    $packageTime = (Get-Item $package).LastWriteTime
    # Код агента — это ещё и Contracts (протокол) с Updater (точка входа на клиенте).
    $paths = @("src/SzDiag.Agent", "src/SzDiag.Contracts", "src/SzDiag.Updater")
    $lastCommit = & git -C $Root log -1 --format="%cI|%h|%s" -- $paths 2>$null
    if (-not $lastCommit) {
        Info "git не ответил — свежесть пакета не проверить"
    } else {
        $parts = $lastCommit -split '\|', 3
        $commitTime = [datetime]::Parse($parts[0])
        if ($commitTime -gt $packageTime) {
            $behind = (& git -C $Root rev-list --count "--since=$($packageTime.ToString('o'))" HEAD -- $paths 2>$null)
            Bad ("пакет агента собран {0:dd.MM HH:mm}, а код агента менялся {1:dd.MM HH:mm} " -f $packageTime, $commitTime)
            Write-Host "       отстал на $behind коммит(ов), последний: $($parts[1]) $($parts[2])" -ForegroundColor Red
            Write-Host "       → .\tools\build-dist.ps1" -ForegroundColor Yellow
        } else {
            Ok ("пакет агента свежий (собран {0:dd.MM HH:mm}, код агента — {1:dd.MM HH:mm})" -f $packageTime, $commitTime)
        }
    }
}

# 1b. CLI/Hub на самом боксе: протухший szcli молча печатал usage вместо ошибки на
# неизвестной команде (`note`), и диагноз занимал минуты — код с ней давно ушёл вперёд,
# а build-dist после этого не гоняли (бэклог п.198/205/211/без номера-165).
function Test-ComponentFreshness([string]$ExePath, [string[]]$Paths, [string]$Label) {
    if (-not (Test-Path $ExePath)) { Bad "$Label не собран ($ExePath)"; return }
    $exeTime = (Get-Item $ExePath).LastWriteTime
    $lastCommit = & git -C $Root log -1 --format="%cI|%h|%s" -- $Paths 2>$null
    if (-not $lastCommit) { Info "$Label — git не ответил, свежесть не проверить"; return }
    $parts = $lastCommit -split '\|', 3
    $commitTime = [datetime]::Parse($parts[0])
    if ($commitTime -gt $exeTime) {
        Bad ("{0} собран {1:dd.MM HH:mm}, а код менялся {2:dd.MM HH:mm}" -f $Label, $exeTime, $commitTime)
        Write-Host "       отстал, последний коммит: $($parts[1]) $($parts[2])" -ForegroundColor Red
        Write-Host "       -> .\tools\build-dist.ps1" -ForegroundColor Yellow
    } else {
        Ok ("{0} свежий (собран {1:dd.MM HH:mm}, код — {2:dd.MM HH:mm})" -f $Label, $exeTime, $commitTime)
    }
}
Test-ComponentFreshness (Join-Path $Root "dist\host\cli\SzDiag.Cli.exe") @("src/SzDiag.Cli", "src/SzDiag.Contracts") "szcli"
Test-ComponentFreshness (Join-Path $Root "dist\host\hub\SzDiag.Hub.exe") @("src/SzDiag.Hub", "src/SzDiag.Contracts") "hub"

# 1c. Непринятая сборка рядом: build-dist.ps1 публикует во временную папку `<out>.new` и
# переименовывает её в `<out>` атомарно ПОСЛЕ успеха — но если целевая папка залочена
# (запущен szcli/hub), переименование может не пройти, и рядом навсегда остаётся свежий
# `cli.new`/`hub.new`, которым никто не пользуется, пока рабочая копия тем временем
# протухает молча (бэклог п.211: `cli` от 07.08, `cli.new` от 19.08, `szcli.cmd`
# по-прежнему указывает на старый `cli`).
foreach ($pair in @(
    @{ Old = "dist\host\cli"; New = "dist\host\cli.new" },
    @{ Old = "dist\host\hub"; New = "dist\host\hub.new" }
)) {
    $newPath = Join-Path $Root $pair.New
    if (-not (Test-Path $newPath)) { continue }
    $oldPath = Join-Path $Root $pair.Old
    $newTime = (Get-ChildItem $newPath -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
    $oldTime = if (Test-Path $oldPath) {
        (Get-ChildItem $oldPath -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
    } else { $null }
    $oldLabel = if ($oldTime) { "рабочая от {0:dd.MM HH:mm}" -f $oldTime } else { "рабочей нет" }
    Bad ("рядом лежит непринятая сборка {0} (от {1:dd.MM HH:mm}) — {2}" -f $pair.New, $newTime, $oldLabel)
    Write-Host "       закрой процесс, который держал файлы (szcli/hub), и перезапусти build-dist.ps1" -ForegroundColor Yellow
}

# 2. Каталог инструментов: из него hub раздаёт тулы клиенту (бэклог п.67)
$hubCfgPath = Join-Path $Root "dist\host\hub\appsettings.json"
if (-not (Test-Path $hubCfgPath)) {
    Bad "конфига hub нет ($hubCfgPath) — прогони build-dist"
} else {
    $cfg = Get-Content $hubCfgPath -Raw | ConvertFrom-Json
    $toolsRoot = $cfg.Hub.ToolsRoot
    if (-not $toolsRoot -or -not (Test-Path $toolsRoot)) {
        Bad "каталог инструментов не найден ($toolsRoot) — build-dist -ToolsRoot <путь>"
    } else {
        $tools = @(Get-ChildItem $toolsRoot -Directory -ErrorAction SilentlyContinue)
        if ($tools.Count -le 1) {
            Bad "в каталоге инструментов почти пусто ($toolsRoot): $($tools.Name -join ', ')"
        } else {
            Ok "инструменты для раздачи: $($tools.Name -join ', ')"
        }
    }
}

# 3. Ключ сервиса: без него доступ не поднять
$key = Join-Path $Root "secrets\svc_diag_key"
if (Test-Path $key) { Ok "ключ сервиса на месте" } else { Bad "нет ключа $key — его генерит build-dist" }

# 4. Клиентский пакет: то, что кладём на машину руками
$clientExe = Join-Path $Root "dist\client\SzDiag.Updater.exe"
if (Test-Path $clientExe) { Ok "клиентский апдейтер собран" } else { Bad "нет $clientExe" }

Write-Host ""
if ($problems.Count -eq 0) {
    Write-Host "Бокс готов к выезду." -ForegroundColor Green
    exit 0
}
Write-Host "Проблем: $($problems.Count). Чинить до заявки, а не посреди неё." -ForegroundColor Red
exit 1
