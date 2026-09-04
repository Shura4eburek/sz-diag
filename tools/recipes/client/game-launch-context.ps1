$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# С чем игра реально стартовала: аргументы процесса, параметры запуска Steam, моды (СЗ 123456).
#
# Грабля: в живом процессе взлетел FSR (amd_fidelityfx_*), а DLSS-модулей нет, хотя файлы целы,
# подписаны и HAGS включён. Значит DLSS отвергается на старте — а это чаще всего внешний
# контекст запуска: ключ вроде -dx11/-nodlss в параметрах Steam или мод в ~mods, подменяющий
# рендер-плагины. Смотрим командную строку процесса, localconfig.vdf и содержимое Paks.
#
#   szcli exec <СЗ> -f tools\recipes\client\game-launch-context.ps1

$ErrorActionPreference = 'SilentlyContinue'

'=== Командная строка процессов игры ==='
Get-CimInstance Win32_Process -Filter "Name like '%Stalker2%'" |
    ForEach-Object { '   pid {0}  {1}' -f $_.ProcessId, $_.CommandLine }

'=== Параметры запуска Steam (localconfig.vdf) ==='
foreach ($cfg in (Get-ChildItem 'C:\Program Files (x86)\Steam\userdata\*\config\localconfig.vdf')) {
    $lines = Get-Content $cfg.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '"1643320"') {          # AppID S.T.A.L.K.E.R. 2
            '   -- ' + $cfg.FullName
            $lines[$i..([Math]::Min($i+25, $lines.Count-1))] | ForEach-Object { '      ' + $_.Trim() }
        }
    }
}

'=== Моды и паки ==='
$paks = 'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl\Stalker2\Content\Paks'
Get-ChildItem $paks -Recurse -Include '*.pak','*.ucas','*.utoc' |
    Where-Object { $_.DirectoryName -match '~mods|LogicMods' -or $_.Name -notmatch '^pakchunk' } |
    Select-Object -First 25 | ForEach-Object { '   {0,-52} {1:dd.MM.yyyy}  {2:N0} б' -f $_.Name, $_.LastWriteTime, $_.Length }
Get-ChildItem $paks -Directory | ForEach-Object { '   папка: {0}' -f $_.Name }

'=== Сторонние DLL рядом с exe (инжекты в рендер) ==='
$bin = 'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl\Stalker2\Binaries\Win64'
Get-ChildItem $bin -File | ForEach-Object {
    $s = Get-AuthenticodeSignature $_.FullName
    '   {0,-46} {1,-14} {2}' -f $_.Name, $s.Status, (($s.SignerCertificate.Subject -split ',')[0])
}

'=== Все загруженные в игру DLL не из папки игры и не из Windows (инжекты оверлеев) ==='
$p = Get-Process Stalker2-Win64-Shipping
$p.Modules | Where-Object { $_.FileName -notmatch 'S\.T\.A\.L\.K\.E\.R|C:\\WINDOWS' } |
    ForEach-Object { '   {0,-34} {1}' -f $_.ModuleName, $_.FileName }
