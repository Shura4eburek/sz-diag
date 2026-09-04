$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что реально загружено в процесс игры: апскейлеры и генераторы кадров (СЗ 123456).
#
# Грабля (стоила неверного вывода): фильтровать модули по именам nvngx_*/sl.*.dll НЕЛЬЗЯ.
# Когда работает DLSS override от NVIDIA App, Streamline грузит свои плагины НЕ из папки игры,
# а из C:\ProgramData\NVIDIA\NGX\models\<фича>\versions\<N>\files\ под безликими именами вида
# 190_E658703.dll и 160_E658700.bin. По именам их не видно — и легко решить, что «DLSS не
# загружен», хотя он загружен. Смотреть надо ПУТЬ модуля, а не имя.
#
# Второе: сам факт загрузки модели (models\dlssd = Ray Reconstruction) не доказывает, что фича
# включена — NGX мапит доступные модели при инициализации. Загруженные модули говорят «фича
# доступна и инициализирована», а не «кадры генерируются». Для «работает ли» нужен замер
# частоты презентов или оверлей NVIDIA.
#
#   szcli exec <СЗ> -f tools\recipes\client\framegen-live-modules.ps1

$ErrorActionPreference = 'SilentlyContinue'

$games = Get-Process | Where-Object { $_.ProcessName -match 'Stalker2|gamelaunchhelper' }
if (-not $games) { '   игра сейчас не запущена — модули проверить не на чем' }

foreach ($g in $games) {
    '=== {0}  pid {1}  старт {2:dd.MM HH:mm:ss}  RAM {3:N0} МБ' -f $g.ProcessName, $g.Id, $g.StartTime, ($g.WorkingSet64/1MB)
    '   путь: {0}' -f $g.Path

    $mods = $g.Modules | Where-Object {
        $_.ModuleName -match 'nvngx|^sl\.|nvapi|amd_fidelity|ffx|libxess' -or
        $_.FileName   -match 'ProgramData\\NVIDIA\\NGX'
    }
    if (-not $mods) { '   ни одного модуля апскейла/FG не загружено'; continue }

    '   -- NVIDIA (папка игры)'
    $mods | Where-Object { $_.ModuleName -match 'nvngx|^sl\.|nvapi' } |
        ForEach-Object { '      {0,-26} {1,-14} {2}' -f $_.ModuleName, $_.FileVersionInfo.FileVersion, $_.FileName }

    '   -- NVIDIA NGX override (ProgramData): фича видна по имени папки, не файла'
    $ngx = $mods | Where-Object { $_.FileName -match 'ProgramData\\NVIDIA\\NGX' }
    $ngx | ForEach-Object {
        $feat = if ($_.FileName -match 'NGX\\models\\([^\\]+)\\') { $matches[1] } else { '?' }
        '      {0,-16} {1}' -f $feat, $_.FileName
    }

    '   -- конкуренты (AMD/Intel)'
    $mods | Where-Object { $_.ModuleName -match 'amd_fidelity|ffx|libxess' } |
        ForEach-Object { '      {0,-42} {1}' -f $_.ModuleName, $_.FileVersionInfo.FileVersion }

    $fgNv  = $ngx | Where-Object { $_.FileName -match '\\(dlssg|sl_dlss_g_\d+)\\' }
    $fgAmd = $mods | Where-Object { $_.ModuleName -match 'framegeneration' }
    $fgInt = $mods | Where-Object { $_.ModuleName -match 'xess_fg' }
    '   >>> DLSS-G инициализирован: {0}   FSR-FG: {1}   XeSS-FG: {2}' -f [bool]$fgNv, [bool]$fgAmd, [bool]$fgInt
    '       (инициализирован ≠ генерирует кадры — движок грузит доступные бэкенды заранее)'
}

'=== Загрузка GPU сейчас ==='
(Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage').CounterSamples |
    Where-Object CookedValue -gt 1 | Sort-Object CookedValue -Descending | Select-Object -First 5 |
    ForEach-Object { '   {0}  {1:N1}%' -f $_.InstanceName, $_.CookedValue }
