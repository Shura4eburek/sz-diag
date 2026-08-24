$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# ГАСИТ ЭНЕРГОСБЕРЕЖЕНИЕ СЕТЕВОГО АДАПТЕРА с бэкапом прежних значений — и откатывает его.
#
# Грабля (СЗ 162367, ПК-клуб на PXE): `net-adapter-power.ps1` нашёл включённые Green Ethernet
# и Gigabit Lite — фирменные режимы Realtek, режущие передатчик и скорость по длине кабеля.
# На длинной трассе до свитча это моргающий линк, а для загрузки по PXE моргнувший линк =
# «не стартує». Крутить это руками в «Дополнительно» свойств адаптера нельзя: правка на
# клиенте без записи прежнего значения — это след, который некому откатить при закрытии СЗ.
#
# ЛОКАЛЬ. Значение задаётся НЕ по видимому тексту: на украинской винде это «Вимкнено», на
# русской «Отключено», на английской «Disabled» — `-DisplayValue 'Disabled'` там просто
# падает. Берём пару ValidDisplayValues/ValidRegistryValues и пишем СЫРОЕ значение реестра.
#
# Смена параметра РЕСЕТИТ адаптер: линк моргнёт, exec-сессия может оборваться на полуслове,
# поэтому всё пишется в лог с flush и гонять надо через --detach:
#   szcli exec <СЗ> -f tools\recipes\client\net-power-off.ps1 --detach
#   szcli exec <СЗ> --result <jobId>

$Restore = $false   # ← $true = вернуть как было из бэкапа (шаг отката при закрытии СЗ)
$Keys = 'Green Ethernet', 'Gigabit Lite'   # ← что гасим (Power Saving Mode добавлять отдельно
                                           #   и осознанно: он же управляет Wake-on-LAN)
$OffMask = 'Disabled|Вимкнено|Отключено|Выключено|Вимк'

$LogDir  = 'C:\ProgramData\szdiag'
$Backup  = Join-Path $LogDir 'net-adv-backup.json'
New-Item -ItemType Directory -Path $LogDir -Force -ErrorAction SilentlyContinue | Out-Null
$Log = Join-Path $LogDir ("net-power-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true
function Say([string]$m) { $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $line; $sw.WriteLine($line) }

try {
    if ($Restore) {
        if (-not (Test-Path $Backup)) { Say "бэкапа $Backup нет — откатывать нечего"; return }
        $saved = Get-Content $Backup -Raw -Encoding UTF8 | ConvertFrom-Json
        Say "откат по бэкапу от $($saved.stamp)"
        foreach ($it in $saved.items) {
            try {
                Set-NetAdapterAdvancedProperty -Name $it.adapter -RegistryKeyword $it.keyword `
                    -RegistryValue $it.oldValue -NoRestart:$false -ErrorAction Stop
                Say "  вернул $($it.adapter) / $($it.display) = $($it.oldDisplay) [$($it.oldValue)]"
            } catch { Say "  ОШИБКА отката $($it.display): $($_.Exception.Message)" }
        }
        Rename-Item $Backup ($Backup -replace '\.json$', ("-done-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')) -ErrorAction SilentlyContinue
        Say 'откат завершён'
        return
    }

    # Бэкап не перезаписываем: второй запуск иначе сохранит УЖЕ выключенное состояние как
    # «прежнее», и откат перестанет что-либо возвращать.
    if (Test-Path $Backup) { Say "ВНИМАНИЕ: бэкап $Backup уже есть — правка, похоже, применена. Выхожу."; return }

    $nics = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up')
    if (-not $nics.Count) { Say 'активных физических адаптеров нет'; return }

    $items = @()
    foreach ($n in $nics) {
        foreach ($k in $Keys) {
            $p = Get-NetAdapterAdvancedProperty -Name $n.Name -ErrorAction SilentlyContinue |
                 Where-Object { $_.DisplayName -like "*$k*" }
            foreach ($prop in @($p)) {
                if ($prop.DisplayValue -match $OffMask) { Say "  $($n.Name) / $($prop.DisplayName) уже выключен — пропускаю"; continue }

                # Ищем СЫРОЕ значение, парное видимому «выключено».
                $idx = -1
                for ($i = 0; $i -lt $prop.ValidDisplayValues.Count; $i++) {
                    if ($prop.ValidDisplayValues[$i] -match $OffMask) { $idx = $i; break }
                }
                if ($idx -lt 0) { Say "  $($prop.DisplayName): среди допустимых нет «выключено» ($($prop.ValidDisplayValues -join ', ')) — пропускаю"; continue }
                $newVal = $prop.ValidRegistryValues[$idx]

                $items += [pscustomobject]@{
                    adapter    = $n.Name
                    display    = $prop.DisplayName
                    keyword    = $prop.RegistryKeyword
                    oldValue   = @($prop.RegistryValue)[0]
                    oldDisplay = $prop.DisplayValue
                    newValue   = $newVal
                }
            }
        }
    }

    if (-not $items.Count) { Say 'менять нечего — всё уже выключено либо параметров нет'; return }

    # Бэкап пишем ДО первой правки: смена параметра ресетит адаптер, связь может оборваться
    # прямо посреди цикла, и без файла откатывать будет нечем.
    [pscustomobject]@{ stamp = (Get-Date).ToString('s'); items = $items } |
        ConvertTo-Json -Depth 5 | Set-Content $Backup -Encoding UTF8
    Say "бэкап прежних значений: $Backup"

    foreach ($it in $items) {
        Say "гашу $($it.adapter) / $($it.display): $($it.oldDisplay) [$($it.oldValue)] -> [$($it.newValue)]"
        try {
            Set-NetAdapterAdvancedProperty -Name $it.adapter -RegistryKeyword $it.keyword `
                -RegistryValue $it.newValue -NoRestart:$false -ErrorAction Stop
        } catch { Say "  ОШИБКА: $($_.Exception.Message)" }
    }

    Start-Sleep -Seconds 8   # адаптер поднимается после ресета не мгновенно
    Say '--- как стало ---'
    foreach ($n in ($items.adapter | Select-Object -Unique)) {
        $a = Get-NetAdapter -Name $n -ErrorAction SilentlyContinue
        Say ("  {0}: {1}, линк {2}" -f $n, $a.Status, $a.LinkSpeed)
        foreach ($k in $Keys) {
            Get-NetAdapterAdvancedProperty -Name $n -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like "*$k*" } |
                ForEach-Object { Say ("     {0,-34} {1}" -f $_.DisplayName, $_.DisplayValue) }
        }
    }
    Say 'ОТКАТ при закрытии СЗ: тот же скрипт с $Restore = $true'
} finally {
    Say "лог: $Log"
    $sw.Close()
}
