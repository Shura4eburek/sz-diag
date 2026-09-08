#requires -Version 5.1
<#
    Отправка сообщения через Telegram-бота уведомлений (@telemart_pc_bot).
    Кому писать — берётся из telegram-users.json (маппинг username -> chatId).

    Запуск на хосте:
        pwsh -File tools\recipes\host\tg-send.ps1 -Text "СЗ 161538: прогон OCCT завершён"
        pwsh -File tools\recipes\host\tg-send.ps1 -To mamoru -Text "..."
        pwsh -File tools\recipes\host\tg-send.ps1 -All -Text "..."
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Text,
    [string] $To,
    [switch] $All,
    [string] $ConfigPath,
    [string] $UsersPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# $PSScriptRoot в дефолтах param() пуст при запуске через powershell -File — резолвим здесь
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)))
if (-not $ConfigPath) { $ConfigPath = Join-Path (Join-Path $root 'secrets') 'telegram.json' }
if (-not $UsersPath)  { $UsersPath  = Join-Path $root 'telegram-users.json' }

$token = (Get-Content -Raw -Encoding UTF8 $ConfigPath | ConvertFrom-Json).token
if (-not $token) { throw "В конфиге нет token: $ConfigPath" }
$users = @((Get-Content -Raw -Encoding UTF8 $UsersPath | ConvertFrom-Json).users) | Where-Object { $_.chatId -ne 0 }
if (-not $users) { throw "В $UsersPath нет ни одного chatId — прогони tg-collect-users.ps1" }

$targets =
    if ($All)   { $users }
    elseif ($To) { $users | Where-Object { $_.username -eq $To.TrimStart('@') } }
    else        { @($users | Where-Object { $_.default })[0]; }
if (-not $targets) { throw "Не найден получатель '$To' в $UsersPath" }

foreach ($t in @($targets)) {
    $body = @{ chat_id = $t.chatId; text = $Text; disable_web_page_preview = $true } | ConvertTo-Json -Compress
    $resp = Invoke-RestMethod -Uri "https://api.telegram.org/bot$token/sendMessage" `
        -Method Post -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if ($resp.ok) { Write-Host "отправлено -> $($t.username) ($($t.chatId))" }
    else          { Write-Warning "не отправлено -> $($t.chatId): $($resp | ConvertTo-Json -Compress)" }
}
