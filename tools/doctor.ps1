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

# R-I4 (ревью волны 1): списки путей ниже раньше писались руками и отставали от реального
# графа зависимостей (`SzDiag.Cli` ссылается на `Kb`/`Hardware`/`ConsoleUi`/`Erp`, `SzDiag.Hub` —
# на `ConsoleUi`/`Kb`, ни один из них не был в проверке) — коммит, тронувший только
# `src/SzDiag.Kb`, давал «szcli свежий» на протухшем exe, ровно тот отказ (п.198/211), ради
# которого сам freshness-guard делался. Считаем транзитивные `<ProjectReference>` из .csproj
# заново при каждом прогоне — список не может отстать от кода, потому что не хранится отдельно.
function Get-TransitiveProjectDirs([string]$csprojRelPath) {
    # [System.IO.Path]::GetRelativePath — .NET Core 2.0+/.NET Standard 2.1, отсутствует в
    # .NET Framework 4.x, на котором работает Windows PowerShell 5.1 (C-6 ревью волны 2:
    # `powershell.exe -File tools\doctor.ps1` падал на первой же проверке методом, которого
    # нет). Считаем относительный путь строкой вручную — без зависимости от рантайма хоста.
    $rootFull = (Resolve-Path $Root).Path.TrimEnd('\', '/')
    $seen = New-Object System.Collections.Generic.HashSet[string]
    $queue = New-Object System.Collections.Generic.Queue[string]
    $queue.Enqueue($csprojRelPath.Replace('\', '/'))
    $result = @()
    while ($queue.Count -gt 0) {
        $rel = $queue.Dequeue()
        if (-not $seen.Add($rel)) { continue }
        $result += (Split-Path $rel -Parent)
        $full = Join-Path $Root $rel
        if (-not (Test-Path $full)) { continue }
        $xml = [xml](Get-Content $full -Raw)
        $refs = @($xml.Project.ItemGroup.ProjectReference.Include) | Where-Object { $_ }
        foreach ($r in $refs) {
            $refFull = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $full -Parent) $r))
            $refRel = $refFull
            if ($refFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                $refRel = $refFull.Substring($rootFull.Length).TrimStart('\', '/')
            }
            $queue.Enqueue($refRel.Replace('\', '/'))
        }
    }
    return $result
}

# 1. Пакет агента: не отстал ли он от кода
$agentDist = Join-Path $Root "dist\host\hub\agent-dist"
$package = Join-Path $agentDist "package.zip"
if (-not (Test-Path $package)) {
    Bad "пакета агента нет ($package) — прогони .\tools\build-dist.ps1"
} else {
    $packageTime = (Get-Item $package).LastWriteTime
    # Код агента — весь транзитивный граф от SzDiag.Agent, плюс весь граф Updater'а (точка
    # входа на клиенте, свой отдельный .csproj, не зависящий от Agent). Раньше Updater
    # дописывался голым литералом и его собственные ссылки (`<ProjectReference>`) не
    # обходились — тот же дрейф ручного списка, который R-I4 убирал, только на уровень ниже.
    $paths = @(Get-TransitiveProjectDirs "src/SzDiag.Agent/SzDiag.Agent.csproj") +
        @(Get-TransitiveProjectDirs "src/SzDiag.Updater/SzDiag.Updater.csproj")
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
Test-ComponentFreshness (Join-Path $Root "dist\host\cli\SzDiag.Cli.exe") (Get-TransitiveProjectDirs "src/SzDiag.Cli/SzDiag.Cli.csproj") "szcli"
Test-ComponentFreshness (Join-Path $Root "dist\host\hub\SzDiag.Hub.exe") (Get-TransitiveProjectDirs "src/SzDiag.Hub/SzDiag.Hub.csproj") "hub"

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

# 2b. Лицензия OCCT (бэклог п.152, #91): на 161716 «тест не запустился» полчаса оказался
# протухшей лицензией `.oke` (истекла ровно в день заявки) плюс файлом-дублем браузера
# `license (2).oke`, который OCCT вообще не подхватывает. Ловим обе граблины до выезда,
# а не по факту мёртвого прогона на клиенте.
function Test-OcctLicense([string]$ToolsRoot) {
    $occtDir = Join-Path $ToolsRoot "occt"
    if (-not (Test-Path $occtDir)) { return }
    $okeFiles = @(Get-ChildItem $occtDir -Filter '*.oke' -File -ErrorAction SilentlyContinue)
    if ($okeFiles.Count -eq 0) {
        Bad "лицензии OCCT нет в $occtDir (*.oke) — тест молча не запустится"
        return
    }
    foreach ($f in $okeFiles) {
        if ($f.Name -notmatch '^[\w.-]+\.oke$') {
            Bad "лицензия OCCT '$($f.Name)' — имя со скобками/пробелом, OCCT такой файл не видит"
            continue
        }
        try {
            $head = (Get-Content $f.FullName -Raw).Split('|')[0]
            $txt = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($head))
            $till = [datetime]::ParseExact($txt.Split(';')[2], 'yyyy/MM/dd', $null)
            $days = ($till.Date - (Get-Date).Date).Days
            if ($days -lt 0) {
                Bad ("лицензия OCCT '{0}' ПРОТУХЛА {1:dd.MM.yyyy} ({2} дн. назад)" -f $f.Name, $till, [math]::Abs($days))
            } elseif ($days -le 14) {
                Bad ("лицензия OCCT '{0}' истекает {1:dd.MM.yyyy} (через {2} дн.)" -f $f.Name, $till, $days)
            } else {
                Ok ("лицензия OCCT '{0}' действует до {1:dd.MM.yyyy} (ещё {2} дн.)" -f $f.Name, $till, $days)
            }
        } catch {
            Bad "лицензия OCCT '$($f.Name)' — не удалось разобрать срок действия"
        }
    }
}
if ($toolsRoot -and (Test-Path $toolsRoot)) { Test-OcctLicense $toolsRoot }

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
