$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Полный контекст того, что NVIDIA App пишет в профиль драйвера по генерации кадров (СЗ 123456).
#
# Зацепка из backend.log: NVIDIA App выставляет в DRS набор NGXOverrideFG — overrideEnabled 0,
# overrideDynamicFGMode 1, overrideDynamicFGTargetFrameRate 0. «Динамический» режим FG с целевой
# частотой 0 — прямой кандидат в причину: фича включена, а генерировать не под что.
# Нужен контекст: к какому приложению это применяется и что там со Smooth Motion.
#
#   szcli exec <СЗ> -f tools\recipes\client\nvapp-fg-override-context.ps1

$ErrorActionPreference = 'SilentlyContinue'
$log = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App\NvBackend\backend.log"
$lines = Get-Content $log

'=== все блоки NGXOverrideFG / Dynamic FG / Smooth Motion (с окружением) ==='
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch 'NGXOverrideFG|DynamicFG|SmoothMotion|Smooth Motion|overrideEnabled|MultiFrame') { continue }
    $from = [Math]::Max(0, $i - 3)
    $to   = [Math]::Min($lines.Count - 1, $i + 1)
    $lines[$from..$to] | ForEach-Object { '   ' + $_.Trim() }
    '   ---'
    $i = $to
}

'=== последние применения профиля к игре ==='
$lines | Select-String -Pattern 'stalker|chornobyl' -SimpleMatch |
    Select-Object -Last 25 | ForEach-Object { '   ' + $_.Line.Trim() }
