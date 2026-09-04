$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Почему Streamline не поднял DLSS/DLSS-G: где лежат его DLL, их версии и ПОДПИСИ (СЗ 123456).
#
# Грабля: в живом процессе игры загружены FSR (amd_fidelityfx_framegeneration_dx12.dll) и XeSS,
# а из NVIDIA — только sl.interposer + nvngx_deepdvc. Ни nvngx_dlss.dll, ни nvngx_dlssg.dll /
# sl.dlss_g.dll не смаплены, хотя в конфиге Method=DLSS/DLSSG. Типовая причина — ручная подмена
# DLL («обновить DLSS» / DLSS Swapper): Streamline проверяет подпись NVIDIA и молча выключает
# фичу, откатываясь на FSR. Поэтому смотрим не только версии, но и подпись каждого файла,
# и то, лежат ли они в папке, откуда Streamline их грузит.
#
#   szcli exec <СЗ> -f tools\recipes\client\dlss-files-integrity.ps1

$ErrorActionPreference = 'SilentlyContinue'
$root = 'E:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl'

'=== Все NVIDIA-DLL в установке игры: путь, версия, подпись ==='
Get-ChildItem $root -Recurse -Include 'nvngx_*.dll','sl.*.dll','nvapi64.dll' |
    Sort-Object FullName | ForEach-Object {
        $s = Get-AuthenticodeSignature $_.FullName
        $subj = ($s.SignerCertificate.Subject -split ',')[0]
        '   {0,-24} {1,-14} {2:dd.MM.yyyy HH:mm}  {3,-14} {4}' -f $_.Name, $_.VersionInfo.FileVersion, $_.LastWriteTime, $s.Status, $subj
        '        {0}' -f $_.DirectoryName
    }

'=== FSR/XeSS для сравнения (что реально взлетело) ==='
Get-ChildItem $root -Recurse -Include 'amd_fidelityfx*.dll','libxess*.dll' |
    ForEach-Object { '   {0,-42} {1,-14} {2:dd.MM.yyyy HH:mm}' -f $_.Name, $_.VersionInfo.FileVersion, $_.LastWriteTime }

'=== Признаки ручной подмены рядом с DLL (бэкапы, DLSS Swapper) ==='
Get-ChildItem $root -Recurse -Include '*.dll.bak','*.dll.old','*_backup*','*dlss*swapper*','*.orig' |
    ForEach-Object { '   {0}  {1:dd.MM.yyyy HH:mm}' -f $_.FullName, $_.LastWriteTime }
'   DLSS Swapper установлен: {0}' -f ((Test-Path "$env:LOCALAPPDATA\Programs\DLSS Swapper") -or (Test-Path "$env:LOCALAPPDATA\dlss-swapper"))
Get-ChildItem "$env:LOCALAPPDATA\dlss-swapper" -Recurse -Depth 2 | Select-Object -First 10 | ForEach-Object { '   ' + $_.FullName }

'=== NVIDIA App: свежие настройки/оверрайды (DLSS override может ломать FG) ==='
$nvapp = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App"
Get-ChildItem $nvapp -Recurse -Include '*.json','*.cfg' -Depth 3 |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-14) } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
    ForEach-Object { '   {0,-56} {1:dd.MM.yyyy HH:mm}' -f $_.Name, $_.LastWriteTime }

'=== Хвост лога текущего запуска ==='
$log = 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Logs\Stalker2.log'
$i = Get-Item $log
'   {0}  изменён {1:dd.MM.yyyy HH:mm:ss}  {2:N0} б' -f $i.Name, $i.LastWriteTime, $i.Length
Get-Content $log -Tail 25 | ForEach-Object { '   ' + $_ }
