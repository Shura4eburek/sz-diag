$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Погасить канал доступа (quick tunnel) ПЕРЕД закрытием СЗ.
#
# Грабля (160697, 17.09.2026): cloudflared, привезённый `szcli push`, держит сам себя —
# `wipe-tools.ps1` не снимает занятый файл, а `szcli close` видит 52 МБ остатка и отказывается
# закрывать СЗ. Обход через `close --skip-leftovers` — это закрытие «по-честному на словах»:
# файл всё равно останется на клиенте. Правильный порядок — снять задачу туннеля, убить
# процесс, снести папку, и только потом `szcli close` (в его revert снятие туннеля всё равно
# идёт первым шагом, так что ничего лишнего мы не ломаем).
#
# SSH после этого недоступен — гонять только когда диагностика закончена.
#   szcli exec <СЗ> -f tools\recipes\client\stop-tunnel.ps1 --param Sz=160697
if (-not (Get-Variable Sz -Scope Script -ErrorAction SilentlyContinue)) { $Sz = $null }
if (-not $Sz) { 'не задан номер СЗ: --param Sz=<номер>'; return }

$task = "szdiag-cfd-$Sz"
'задача: ' + (schtasks /end /tn $task 2>&1)
Start-Sleep -Seconds 2

$p = Get-Process cloudflared -ErrorAction SilentlyContinue
if ($p) {
    $p | ForEach-Object { "убиваю cloudflared pid=$($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
} else { 'процесс cloudflared не найден' }

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'агент не найден — путь к tools\ не резолвится'; return }
$dir = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\cloudflared'
if (-not (Test-Path $dir)) { 'папки cloudflared нет — сносить нечего'; return }

$mb = [math]::Round((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $dir) { "⚠ НЕ снято (всё ещё занят): $dir" } else { "снято: $dir ($mb МБ)" }
