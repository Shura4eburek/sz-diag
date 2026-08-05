<#
.SYNOPSIS
    Ручной оффсайт-бэкап базы знаний: коммитит изменения в kb и пушит в приватный репо.

.DESCRIPTION
    Штатно бэкап делает сам hub (KbBackupService: на старте, каждые 15 минут и при
    остановке; настройки — секция Hub.KbBackup в appsettings.json). Этот скрипт нужен,
    когда hub не поднят, а выгрузить надо сейчас. Если в vault ничего не менялось —
    завершается тихо, без пустых коммитов. Всё пишется в лог рядом с kb, чтобы
    молчаливый отвал (сеть, протухшие креды) можно было увидеть постфактум,
    а не обнаружить в момент, когда бэкап понадобился.

.PARAMETER KbPath
    Путь к vault. По умолчанию — dist\host\kb относительно корня репозитория.

.PARAMETER Uninstall
    Снять старую задачу планировщика SzDiag-KbBackup (расписание переехало в hub).

.EXAMPLE
    .\tools\kb-backup.ps1              # разовый прогон вручную
    .\tools\kb-backup.ps1 -Uninstall   # снести задачу планировщика
#>
[CmdletBinding()]
param(
    [string]$KbPath,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$TaskName = 'SzDiag-KbBackup'

if (-not $KbPath) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $KbPath = Join-Path $repoRoot 'dist\host\kb'
}
$KbPath = [System.IO.Path]::GetFullPath($KbPath)
$logPath = Join-Path (Split-Path -Parent $KbPath) 'kb-backup.log'

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    try { Add-Content -Path $logPath -Value $line -Encoding utf8 } catch { }
}

# --- снятие старой задачи планировщика ----------------------------------------
# Расписание живёт в hub (KbBackupService); задача осталась только как хвост на
# машинах, где её успели поставить.

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Log "Задача '$TaskName' снята"
    return
}

# --- собственно бэкап ---------------------------------------------------------

if (-not (Test-Path (Join-Path $KbPath '.git'))) {
    Write-Log "Не git-репозиторий: $KbPath" 'ERROR'
    exit 1
}

Push-Location $KbPath
try {
    git add -A
    if ($LASTEXITCODE -ne 0) { Write-Log "git add вернул $LASTEXITCODE" 'ERROR'; exit 1 }

    $pending = git status --porcelain
    if (-not $pending) {
        # Тихий выход: пустых коммитов не плодим, лог не засоряем.
        exit 0
    }

    $changed = ($pending | Measure-Object).Count
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
    $msgFile = Join-Path $env:TEMP "kb-backup-msg-$PID.txt"
    # Строго UTF-8 БЕЗ BOM: Set-Content -Encoding utf8 в PS 5.1 лепит BOM, и git
    # утаскивает его в первую строку сообщения коммита.
    [System.IO.File]::WriteAllText(
        $msgFile,
        "kb: автосохранение $stamp ($changed файл(ов))",
        (New-Object System.Text.UTF8Encoding $false))

    git commit -F $msgFile
    $commitCode = $LASTEXITCODE
    Remove-Item $msgFile -ErrorAction SilentlyContinue
    if ($commitCode -ne 0) { Write-Log "git commit вернул $commitCode" 'ERROR'; exit 1 }

    # Без 2>&1: PS 5.1 заворачивает stderr нативной команды в NativeCommandError
    # и роняет прогон даже при успешном push (git пишет прогресс в stderr).
    git push origin main
    if ($LASTEXITCODE -ne 0) {
        # Коммит уже лёг локально — данные не потеряны, уедут следующим прогоном.
        Write-Log "git push вернул $LASTEXITCODE — изменения закоммичены локально, но НЕ выгружены" 'WARN'
        exit 1
    }

    Write-Log "Выгружено: $changed файл(ов)"
}
finally {
    Pop-Location
}
