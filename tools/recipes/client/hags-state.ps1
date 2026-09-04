$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Фактическое состояние HAGS (Hardware-accelerated GPU scheduling) — не по реестру, а как его
# видит система (СЗ 123456).
#
# Грабля: отсутствие HwSchMode в реестре само по себе ничего не доказывает — ключ может быть
# не создан ни разу, а состояние всё равно «выключено по умолчанию». Единственный честный
# источник без GUI — dxdiag /t: строка "Hardware-accelerated GPU scheduling"/"Аппаратное
# ускорение планирования GPU". dxdiag под SYSTEM в session 0 отрабатывает, но медленно —
# ждём до 90 с.
#
#   szcli exec <СЗ> -f tools\recipes\client\hags-state.ps1

$ErrorActionPreference = 'SilentlyContinue'
$out = Join-Path $env:TEMP 'szdiag-dxdiag.txt'
Remove-Item $out -Force
$p = Start-Process dxdiag -ArgumentList '/whql:off','/t',$out -PassThru -WindowStyle Hidden
$null = $p.WaitForExit(90000)
if (-not (Test-Path $out)) { '   dxdiag не создал отчёт'; return }

'=== dxdiag: планировщик GPU и связанные фичи ==='
Get-Content $out |
    Select-String -Pattern 'GPU scheduling|планирован|Variable Refresh|Hardware-accelerated|MPO|DisplayPort|Card name|Driver Version|Driver Date|Feature Levels|DirectX Version|Miracast' |
    ForEach-Object { '   ' + $_.Line.Trim() }

'=== Streamline/DLSS логи рядом с игрой ==='
$gameRoot = 'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl'
Get-ChildItem $gameRoot -Recurse -Include 'sl.log','sl*.log','nvngx*.log','*streamline*.log' -Depth 6 |
    ForEach-Object { '   {0}  {1:dd.MM.yyyy HH:mm}  {2:N0} б' -f $_.FullName, $_.LastWriteTime, $_.Length }
Get-ChildItem "$env:LOCALAPPDATA\..\..\Documents" -Recurse -Include 'sl.log' -Depth 3 |
    ForEach-Object { '   {0}  {1:dd.MM.yyyy HH:mm}' -f $_.FullName, $_.LastWriteTime }
