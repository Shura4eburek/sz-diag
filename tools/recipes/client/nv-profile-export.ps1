$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Настройки профиля драйвера для игры — то, чего не видно ни в реестре, ни в конфигах (СЗ 123456).
#
# Грабля: база профилей NVIDIA (nvdrsdb0.bin) бинарная, из неё вытаскиваются только имена
# приложений; сами значения (Low Latency Mode, V-Sync, Max Frame Rate, DLSS override) прочитать
# нечем. nvidiaProfileInspector умеет выгрузить всю базу в XML одной командой без GUI:
#   nvidiaProfileInspector.exe -export <файл>
# Именно там сидят классические блокираторы генерации кадров — Ultra Low Latency и форсированный
# V-Sync: в игре переключатель FG стоит «вкл», а драйвер фичу не пускает.
#
# Доставка: szcli push <СЗ> nvinspector
#
#   szcli exec <СЗ> -f tools\recipes\client\nv-profile-export.ps1

$ErrorActionPreference = 'SilentlyContinue'
$tool = 'C:\Users\nekit\Desktop\client\tools\nvinspector\nvidiaProfileInspector.exe'
$out  = 'C:\Windows\Temp\szdiag-nvprofiles.xml'

if (-not (Test-Path $tool)) { '   инструмент не доставлен: szcli push <СЗ> nvinspector'; return }
Remove-Item $out -Force
$p = Start-Process $tool -ArgumentList '-export', $out -PassThru -WindowStyle Hidden -WorkingDirectory (Split-Path $tool)
$null = $p.WaitForExit(90000)
if (-not (Test-Path $out)) { '   экспорт не создан (exit {0})' -f $p.ExitCode; return }
'   экспорт: {0:N0} б' -f (Get-Item $out).Length

[xml]$xml = Get-Content $out
$profiles = $xml.ArrayOfProfile.Profile
'   профилей в базе: {0}' -f $profiles.Count

'=== профиль игры ==='
$game = $profiles | Where-Object {
    $_.ProfileName -match 'STALKER 2|Chornobyl|Chernobyl' -or
    ($_.Executeables.string -join ' ') -match 'stalker2-win64-shipping'
}
foreach ($g in $game) {
    '   имя профиля: {0}' -f $g.ProfileName
    '   exe: {0}' -f (($g.Executeables.string) -join ', ')
    foreach ($s in $g.Settings.ProfileSetting) {
        '      {0,-46} = {1}' -f $s.SettingNameInfo, $s.SettingValue
    }
}
if (-not $game) { '   персонального профиля нет — действуют только глобальные настройки' }

'=== глобальный профиль (Base Profile) ==='
$base = $profiles | Where-Object { $_.ProfileName -match '^Base Profile$|_GLOBAL_DRIVER_PROFILE' }
foreach ($s in $base.Settings.ProfileSetting) {
    '      {0,-46} = {1}' -f $s.SettingNameInfo, $s.SettingValue
}
