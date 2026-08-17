$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Поочерёдно гасит фоновые приложения пользовательской сессии и после каждого меряет pstate —
# ищем то, что держит карту в максимальном режиме на простое.
#
# Грабля (СЗ 161190): карта уходит в P0 (1792/7201, вентилятор 71 %) ровно на входе в сессию,
# до логина честно стоит в P8 с остановленным вентилятором. Значит виновник живёт в автозапуске
# пользователя. Типовые кандидаты — приложения на CEF/Electron с аппаратным ускорением
# (uTorrent web client, Discord, WebView2): они не грузят GPU (util 0 %), но не дают ему
# опуститься в idle-pstate, и вентилятор законно молотит. Гасить надо по одному с паузой:
# PowerMizer опускает pstate не мгновенно.
#
# Ничего не удаляется — только завершение процессов, всё вернётся после перезапуска сессии.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-suspects.ps1

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (-not (Test-Path $smi)) { 'nvidia-smi не найден'; return }
function Pstate { (& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

"== старт: $(Pstate)"
'== процессы с окнами и фоновые потребители GPU'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -gt 0 } |
    Sort-Object ProcessName | Select-Object -Unique ProcessName |
    ForEach-Object { $_.ProcessName } | Join-String -Separator ', '

# Порядок — от самых частых виновников к системным.
$order = @('uTorrentClients', 'uTorrent', 'Discord', 'msedgewebview2', 'msedge', 'PhoneExperienceHost',
    'CrossDeviceResume', 'LEDKeeper2', 'SearchHost', 'ShellHost')
foreach ($name in $order) {
    $p = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if (-not $p.Count) { continue }
    "-- гасим $name ($($p.Count) шт)"
    $p | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { "   не убился pid=$($_.Id): $($_.Exception.Message)" } }
    for ($i = 0; $i -lt 3; $i++) { Start-Sleep -Seconds 5; "   +$((($i + 1) * 5))с  $(Pstate)" }
}

'== итог'
"   $(Pstate)"
'   (P8 + fan 0 % => виновник в списке выше; P0 остался => дело не в приложениях сессии)'
