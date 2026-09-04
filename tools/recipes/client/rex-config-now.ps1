$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что игра держит в настройках ПОСЛЕ перезапуска — не откатила ли она FG сама (СЗ 123456).
#
# Грабля: PresentMon показал, что кадры не генерируются ни до, ни после рестарта — причём ни одним
# бэкендом (в процессе висят и DLSS-G, и FSR-FG, и XeSS-FG). Если после старта игра переписала
# GSCReXFrameGeneration.ini на Method=None/Mode=Off — значит движок сам отверг FG, и искать надо
# условие отказа, а не настройку. Если Method=DLSSG уцелел — настройка доходит, но не действует.
#
#   szcli exec <СЗ> -f tools\recipes\client\rex-config-now.ps1

$ErrorActionPreference = 'SilentlyContinue'
$cfg = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows'

foreach ($f in (Get-ChildItem $cfg -Filter 'GSCReX*.ini')) {
    '=== {0}   изменён {1:dd.MM HH:mm:ss} ===' -f $f.Name, $f.LastWriteTime
    Get-Content $f.FullName | ForEach-Object { '   ' + $_ }
}

'=== GameUserSettings: режим окна, V-Sync, лимит ==='
Get-Content (Join-Path $cfg 'GameUserSettings.ini') |
    Select-String -Pattern 'bUseVSync|FullscreenMode|FrameRateLimit|bUseDynamicResolution|ResolutionSize|GraphicsAdapter|Swapchain' |
    ForEach-Object { '   ' + $_.Line.Trim() }

'=== Engine.ini пользователя (сюда кладут CVar-переопределения) ==='
$eng = Join-Path $cfg 'Engine.ini'
if (Test-Path $eng) { Get-Content $eng | ForEach-Object { '   ' + $_ } } else { '   (нет файла)' }

'=== лог текущего запуска: есть ли вообще категории DLSS/Streamline ==='
$log = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Logs\Stalker2.log'
$i = Get-Item $log
'   {0}  {1:N0} б  изменён {2:HH:mm:ss}' -f $i.Name, $i.Length, $i.LastWriteTime
Get-Content $log | Select-String -Pattern 'LogDLSS|LogStreamline|LogNGX|LogReX|GSCReX|DLSSG|FrameGeneration|LogRHI|D3D12RHI' |
    Select-Object -First 40 | ForEach-Object { '   ' + $_.Line.Trim() }
