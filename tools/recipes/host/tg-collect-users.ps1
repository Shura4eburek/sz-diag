#requires -Version 5.1
<#
    Собирает id пользователей Telegram-бота уведомлений и пишет их в telegram-users.json.

    Грабля, ради которой написан: бот знает, кому писать, только после того как человек
    сам написал ему первым — chat_id ниоткуда больше не берётся, а getUpdates хранит
    апдейты всего 24 часа и отдаёт их ОДИН раз (после подтверждения offset они пропадают).
    Поэтому: попросил человека написать боту -> сразу прогнал этот скрипт.

    Запуск на хосте:
        pwsh -File tools\recipes\host\tg-collect-users.ps1
        pwsh -File tools\recipes\host\tg-collect-users.ps1 -Wait 60   # ждать сообщения
#>
[CmdletBinding()]
param(
    [string] $ConfigPath,
    [string] $UsersPath,
    [int]    $Wait = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# $PSScriptRoot в дефолтах param() пуст при запуске через powershell -File — резолвим здесь
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)))
if (-not $ConfigPath) { $ConfigPath = Join-Path (Join-Path $root 'secrets') 'telegram.json' }
if (-not $UsersPath)  { $UsersPath  = Join-Path $root 'telegram-users.json' }

if (-not (Test-Path $ConfigPath)) { throw "Нет конфига бота: $ConfigPath" }
$token = (Get-Content -Raw -Encoding UTF8 $ConfigPath | ConvertFrom-Json).token
if (-not $token) { throw "В конфиге нет token: $ConfigPath" }

$api = "https://api.telegram.org/bot$token"
$query = if ($Wait -gt 0) { "?timeout=$Wait" } else { '' }
Write-Host "Опрашиваю getUpdates$(if ($Wait -gt 0) { " (жду до $Wait с)" })..."
$resp = Invoke-RestMethod -Uri "$api/getUpdates$query" -TimeoutSec ($Wait + 30)
if (-not $resp.ok) { throw "Telegram вернул ошибку: $($resp | ConvertTo-Json -Compress)" }

$found = @{}
foreach ($u in $resp.result) {
    $msg = $u.message; if (-not $msg) { $msg = $u.edited_message }
    if (-not $msg) { continue }
    $chat = $msg.chat
    $name = if ($chat.username) { $chat.username } else { (@($chat.first_name, $chat.last_name) -ne $null) -join ' ' }
    $found[[string]$chat.id] = [pscustomobject]@{ username = $name; chatId = [int64]$chat.id }
}

if ($found.Count -eq 0) {
    Write-Warning 'Новых сообщений нет. Пусть человек напишет боту /start и прогони скрипт снова.'
    exit 1
}

$doc = Get-Content -Raw -Encoding UTF8 $UsersPath | ConvertFrom-Json
$list = New-Object System.Collections.ArrayList
foreach ($u in @($doc.users)) { if ($u.chatId -ne 0) { [void]$list.Add($u) } }

foreach ($f in $found.Values) {
    $exists = $list | Where-Object { $_.chatId -eq $f.chatId } | Select-Object -First 1
    if ($exists) {
        $exists.username = $f.username
        Write-Host "обновлён: $($f.username) ($($f.chatId))"
    } else {
        [void]$list.Add([pscustomobject]@{
            username = $f.username
            chatId   = $f.chatId
            note     = ''
            default  = ($list.Count -eq 0)
        })
        Write-Host "добавлен: $($f.username) ($($f.chatId))"
    }
}

$doc.users = @($list)
($doc | ConvertTo-Json -Depth 5) | Set-Content -Path $UsersPath -Encoding UTF8
Write-Host "Записано в $UsersPath"
