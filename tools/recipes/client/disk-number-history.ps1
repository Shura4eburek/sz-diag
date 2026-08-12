param([int]$Days = 30)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто был \Device\HarddiskN в момент дисковых ошибок в журнале.
#
# Грабля (СЗ 161346): события `disk 7` (bad block) и `stornvme 129` (reset контроллера) в журнале
# указывают на \Device\Harddisk1\DR1 и \Device\RaidPort2, и по СЕГОДНЯШНЕЙ карте это диск F: из
# заказа. Но клиент справедливо возразил: в те дни он подключал флешки, а нумерация Harddisk
# зависит от порядка подключения — то есть Harddisk1 тогда мог быть вообще другим устройством.
# Этот рецепт восстанавливает историю нумерации по журналу Partition/Diagnostic (событие 1006
# пишется при каждом подключении диска и несёт DiskNumber + модель + серийник) и сверяет её
# со временем дисковых ошибок.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\disk-number-history.ps1 --timeout 240

$since = (Get-Date).AddDays(-$Days)

'=== Карта дисков СЕЙЧАС ==='
Get-CimInstance Win32_DiskDrive | Sort-Object Index |
    Select-Object Index, Model, SerialNumber, InterfaceType, SCSIPort,
        @{n = 'GB'; e = { [math]::Round($_.Size / 1GB) } } |
    Format-Table -AutoSize | Out-String -Width 200

''
'=== История нумерации: Partition/Diagnostic 1006 (пишется при подключении диска) ==='
$part = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-Partition/Diagnostic'
        Id        = 1006
        StartTime = $since
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)

if (-not $part) {
    '  событий нет (канал Partition/Diagnostic пуст или отключён)'
}
else {
    "  всего событий: $($part.Count)"
    ''
    foreach ($e in $part) {
        $x = [xml]$e.ToXml()
        $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $model = ($d['Model'] -replace '\s+', ' ').Trim()
        $cap   = if ($d['Capacity']) { [math]::Round([double]$d['Capacity'] / 1GB) } else { '?' }
        # Adapter — это номер N в \Device\RaidPortN из событий stornvme, Bus — аппаратный
        # PCI-слот (не плывёт). DiskNumber и Adapter плавают от загрузки к загрузке, поэтому
        # архивные ошибки привязывать к железу можно ТОЛЬКО по этой истории, а не по карте «сейчас».
        "{0:dd.MM.yyyy HH:mm:ss}  Disk {1,-3} RaidPort {2,-3} PCI-bus {3,-3} {4,-22} {5} ГБ" -f `
            $e.TimeCreated, $d['DiskNumber'], $d['Adapter'], $d['Bus'], $model, $cap
    }
}

''
'=== USB-накопители, которые вообще подключались к машине (реестр USBSTOR) ==='
# 0064 = первое подключение, 0066 = последнее подключение, 0067 = последнее извлечение
$props = @{ '0064' = 'первое'; '0066' = 'последнее подкл'; '0067' = 'последнее извл' }
$found = $false
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\USBSTOR' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $devClass = $_.PSChildName
        Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
            $found = $true
            $inst = $_
            $name = (Get-ItemProperty $inst.PSPath -ErrorAction SilentlyContinue).FriendlyName
            $line = "  $devClass | $name"
            foreach ($k in '0064', '0066', '0067') {
                $p = Join-Path $inst.PSPath "Properties\{83da6326-97a6-4088-9453-a1923f573b29}\$k"
                $v = (Get-ItemProperty $p -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
                if ($v) {
                    try { $line += " | $($props[$k]): $([datetime]::FromFileTime([bitconverter]::ToInt64($v,0)).ToString('dd.MM.yyyy HH:mm'))" } catch {}
                }
            }
            $line
        }
    }
if (-not $found) { '  USB-накопителей в реестре нет' }

''
'=== Дисковые ошибки за период (с кем сверять нумерацию) ==='
$err = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        ProviderName = @('disk', 'stornvme', 'storahci', 'Ntfs', 'volmgr')
        StartTime    = $since
    } -ErrorAction SilentlyContinue |
    Where-Object { $_.LevelDisplayName -in 'Ошибка', 'Error', 'Предупреждение', 'Warning', 'Помилка', 'Попередження' } |
    Sort-Object TimeCreated)

if (-not $err) { '  за период нет' }
foreach ($e in $err) {
    $msg = ($e.Message -replace '\s+', ' ').Trim()
    if ($msg.Length -gt 150) { $msg = $msg.Substring(0, 150) }
    "{0:dd.MM.yyyy HH:mm:ss}  {1,-10} Id={2,-4} {3}" -f $e.TimeCreated, $e.ProviderName, $e.Id, $msg
}
