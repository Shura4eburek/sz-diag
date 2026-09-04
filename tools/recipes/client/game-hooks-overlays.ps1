$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто ещё сидит в свопчейне игры: хуки оверлеев и захвата (СЗ 123456).
#
# Зачем: генерация кадров подменяет свопчейн (r.D3D12.PreferredSwapchainProvider =
# FStreamlineD3D12DXGISwapchainProvider). Любой сторонний хук, влезший в презент раньше —
# оверлей или захват трансляции — может не дать Streamline вставить сгенерированный кадр.
# На скриншоте экрана клиента видно, что идёт трансляция Discord Go Live («В ЭФИРЕ») и висит
# оверлей мониторинга, а измерение показывает ровно один презент на кадр движка.
#
#   szcli exec <СЗ> -f tools\recipes\client\game-hooks-overlays.ps1

$ErrorActionPreference = 'SilentlyContinue'
$p = Get-Process Stalker2-Win64-Shipping
if (-not $p) { '   игра не запущена'; return }
'   pid {0}, старт {1:HH:mm:ss}' -f $p.Id, $p.StartTime

'=== модули не из папки игры и не системные (кандидаты в хуки) ==='
$p.Modules | Where-Object { $_.FileName -notmatch 'S\.T\.A\.L\.K\.E\.R|C:\\WINDOWS|ProgramData\\NVIDIA' } |
    ForEach-Object { '   {0,-34} {1}' -f $_.ModuleName, $_.FileName }

'=== известные оверлеи/захват в модулях ==='
$hooks = $p.Modules | Where-Object { $_.ModuleName -match 'discord|overlay|obs|rtss|hook|xbox|gamebar|nvcamera|nvfbc|medal|streamlab|Overwolf' }
if ($hooks) { $hooks | ForEach-Object { '   {0,-34} {1}' -f $_.ModuleName, $_.FileName } }
else        { '   в процессе игры чужих хуков захвата нет' }

'=== процессы оверлеев/трансляции сейчас ==='
Get-Process | Where-Object { $_.ProcessName -match 'Discord|obs|RTSS|Afterburner|Overwolf|Medal|Streamlabs|GameBar|nvcontainer|NVIDIA' } |
    Sort-Object ProcessName | ForEach-Object { '   {0,-26} pid {1,-7} RAM {2,7:N0} МБ  старт {3:HH:mm}' -f $_.ProcessName, $_.Id, ($_.WorkingSet64/1MB), $_.StartTime }

'=== кто рисует оверлей мониторинга (окна поверх игры) ==='
Add-Type -AssemblyName System.Windows.Forms
Get-Process | Where-Object { $_.MainWindowHandle -ne 0 } |
    ForEach-Object { '   {0,-26} «{1}»' -f $_.ProcessName, $_.MainWindowTitle }

'=== аппаратный захват NVIDIA (ShadowPlay/мгновенный повтор) ==='
$nvsp = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App\NvBackend\storage.json"
if (Test-Path $nvsp) {
    (Get-Content $nvsp -Raw) -split ',' | Where-Object { $_ -match 'InstantReplay|ShadowPlay|Recording|Overlay|IGX' } |
        Select-Object -First 15 | ForEach-Object { '   ' + $_.Trim() }
}
