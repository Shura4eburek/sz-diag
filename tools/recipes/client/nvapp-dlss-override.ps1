$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что NVIDIA App навязывает игре поверх её настроек: DLSS override и профили (СЗ 123456).
#
# Грабля: файлы DLSS в игре целые и подписанные, HAGS включён, а в живом процессе нет ни
# nvngx_dlss.dll, ни nvngx_dlssg.dll — вместо них FSR. Следующий подозреваемый — NVIDIA App:
# ngxdlssoverridestate.json / overrides.json меняли ровно в момент возни с настройками
# (23:29 и 23:38). Override «Frame Generation: Off» или чужая модель DLSS отключают фичу молча.
#
#   szcli exec <СЗ> -f tools\recipes\client\nvapp-dlss-override.ps1

$ErrorActionPreference = 'SilentlyContinue'
$nvapp = "$env:LOCALAPPDATA\NVIDIA Corporation\NVIDIA App"

foreach ($name in 'ngxdlssoverridestate.json','overrides.json','ApplicationStorage.json','profiles_metadata.json') {
    foreach ($f in (Get-ChildItem $nvapp -Recurse -Filter $name -Depth 4)) {
        "=== $($f.FullName)  ($($f.Length) б, изменён $($f.LastWriteTime.ToString('dd.MM.yyyy HH:mm')))"
        $txt = Get-Content $f.FullName -Raw
        if ($f.Name -eq 'ApplicationStorage.json' -and $txt.Length -gt 4000) {
            # огромный список всех игр — вытаскиваем только куски про Stalker
            $idx = 0
            while (($idx = $txt.IndexOf('talker', $idx)) -ge 0) {
                $from = [Math]::Max(0, $idx - 600); $len = [Math]::Min(1200, $txt.Length - $from)
                '   ...' + $txt.Substring($from, $len).Replace("`r"," ").Replace("`n"," ") + '...'
                $idx += 6
                if ($idx -gt $txt.Length) { break }
            }
        } else {
            $txt -split "`n" | Select-Object -First 120 | ForEach-Object { '   ' + $_.TrimEnd() }
        }
    }
}

'=== Реестр NGX: глобальные оверрайды драйвера ==='
foreach ($k in 'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NGXCore','HKCU:\SOFTWARE\NVIDIA Corporation\Global\NGXCore') {
    if (Test-Path $k) {
        "   -- $k"
        (Get-ItemProperty $k).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } |
            ForEach-Object { '      {0,-40} = {1}' -f $_.Name, $_.Value }
    }
}
