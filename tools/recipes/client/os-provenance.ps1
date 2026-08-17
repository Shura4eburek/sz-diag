[Console]::OutputEncoding = [Text.Encoding]::UTF8
# ОТКУДА ВЗЯЛАСЬ ЭТА ОС: чистая установка на этой машине или раскатанный образ (Acronis/sysprep).
#
# Грабля (СЗ 161346): в отчёте клиенту написали «ОС старше даты сборки, значит переносилась,
# а не ставилась с нуля» — на основании ОДНОГО InstallDate. Клиент превратил это в претензию
# «мне развернули двухлетний образ вместо чистой установки» и требует письменного подтверждения.
# InstallDate сам по себе ничего не доказывает: он переживает feature update и едет внутри образа.
# Нужны прямые признаки: дата создания тома, таймстампы системных папок, следы sysprep/clone,
# начало setupapi.dev.log, призраки чужого железа в Enum и сторонние драйверы не под эту платформу.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\os-provenance.ps1 --timeout 300

function Hr($t) { ''; "=== $t ==="; }

Hr 'Версия и заявленная дата установки'
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
"ProductName      : {0}" -f $cv.ProductName
"DisplayVersion   : {0} (build {1}.{2})" -f $cv.DisplayVersion, $cv.CurrentBuild, $cv.UBR
"EditionID        : {0}" -f $cv.EditionID
"InstallationType : {0}" -f $cv.InstallationType
if ($cv.InstallDate) {
    "InstallDate      : {0:dd.MM.yyyy HH:mm:ss} (реестр, unix)" -f ([DateTimeOffset]::FromUnixTimeSeconds($cv.InstallDate).LocalDateTime)
}
if ($cv.InstallTime) {
    "InstallTime      : {0:dd.MM.yyyy HH:mm:ss} (реестр, FILETIME)" -f ([DateTime]::FromFileTime($cv.InstallTime))
}
"BuildLabEx       : {0}" -f $cv.BuildLabEx

Hr 'Дата создания тома C: и системных папок (таймстампы едут внутри образа)'
# CreationTime "System Volume Information" = момент форматирования тома; если он свежий,
# а C:\Windows старая — файлы приехали из образа с сохранением дат.
foreach ($p in 'C:\System Volume Information', 'C:\Windows', 'C:\Program Files', 'C:\Users', 'C:\Windows\System32\config\SOFTWARE', 'C:\pagefile.sys') {
    $i = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
    if ($i) { "  {0,-46} создан {1:dd.MM.yyyy HH:mm:ss}  изменён {2:dd.MM.yyyy HH:mm:ss}" -f $p, $i.CreationTime, $i.LastWriteTime }
    else { "  {0,-46} нет доступа/не существует" -f $p }
}
'Разделы диска (дата создания раздела не хранится; смотрим только разметку):'
Get-Partition -ErrorAction SilentlyContinue |
    Select-Object DiskNumber, PartitionNumber, DriveLetter, @{n='GB';e={[math]::Round($_.Size/1GB,1)}}, Type, GptType |
    Format-Table -Auto | Out-String -Width 160

Hr 'Профили пользователей (дата создания = первый вход на ЭТОЙ системе)'
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction SilentlyContinue | ForEach-Object {
    $pp = (Get-ItemProperty $_.PSPath).ProfileImagePath
    if ($pp -and $pp -notmatch 'systemprofile|LocalService|NetworkService') {
        $d = Get-Item -LiteralPath $pp -Force -ErrorAction SilentlyContinue
        if ($d) { "  {0,-34} создан {1:dd.MM.yyyy HH:mm:ss}" -f $pp, $d.CreationTime }
        else { "  {0,-34} папки нет (профиль удалён)" -f $pp }
    }
}
'Локальные учётки:'
Get-LocalUser -ErrorAction SilentlyContinue |
    Select-Object Name, Enabled, @{n='Создана';e={$_.PasswordLastSet}}, LastLogon |
    Format-Table -Auto | Out-String -Width 160

Hr 'Следы клонирования / sysprep'
foreach ($k in 'HKLM:\SYSTEM\Setup', 'HKLM:\SYSTEM\Setup\Status\SysprepStatus', 'HKLM:\SYSTEM\Setup\Status') {
    $v = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($v) {
        "  [$k]"
        $v.PSObject.Properties |
            Where-Object { $_.Name -notmatch '^PS' } |
            ForEach-Object { "     {0,-26} = {1}" -f $_.Name, $_.Value }
    }
}
# CloneTag появляется, когда том/систему сняли в образ и развернули
$ct = Get-ItemProperty 'HKLM:\SYSTEM\Setup' -Name CloneTag -ErrorAction SilentlyContinue
if ($ct) { "  CloneTag: {0}" -f ($ct.CloneTag -join ' | ') } else { '  CloneTag: нет' }
"  Windows.old: {0}" -f $(if (Test-Path 'C:\Windows.old') { 'ЕСТЬ' } else { 'нет' })
"  Panther (логи установки): {0}" -f $(
    $pa = Get-ChildItem 'C:\Windows\Panther' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
    if ($pa) { "{0} файлов, первый {1:dd.MM.yyyy}, последний {2:dd.MM.yyyy}" -f $pa.Count, $pa[0].LastWriteTime, $pa[-1].LastWriteTime } else { 'нет' })

Hr 'setupapi.dev.log — когда на этой системе впервые ставились устройства'
$sa = 'C:\Windows\INF\setupapi.dev.log'
if (Test-Path $sa) {
    $fi = Get-Item $sa -Force
    "  файл: создан {0:dd.MM.yyyy HH:mm:ss}, изменён {1:dd.MM.yyyy HH:mm:ss}, {2:N1} МБ" -f $fi.CreationTime, $fi.LastWriteTime, ($fi.Length/1MB)
    '  первые 25 строк (начало журнала = момент первой установки драйверов):'
    Get-Content $sa -TotalCount 25 -ErrorAction SilentlyContinue | ForEach-Object { "    $_" }
    '  первые записи об установке устройств:'
    Select-String -Path $sa -Pattern '^>>>  \[Device Install' -ErrorAction SilentlyContinue |
        Select-Object -First 5 | ForEach-Object { "    {0}" -f $_.Line.Trim() }
} else { '  файла нет' }

Hr 'Призраки чужого железа (устройства, которых сейчас в машине нет)'
# Если ОС раскатана образом с ДРУГОЙ платформы — в Enum остаются её PCI-устройства
$ghosts = @(Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.Status -eq 'Unknown' -and $_.InstanceId -match '^(PCI|USB\\VID)' })
"  всего призраков PCI/USB: {0}" -f $ghosts.Count
$ghosts | Select-Object -First 40 | ForEach-Object { "    {0,-10} {1}" -f $_.Class, $_.FriendlyName }

Hr 'Сторонние драйверы в DriverStore (oem*.inf) — не под эту платформу?'
$drv = @(Get-WindowsDriver -Online -ErrorAction SilentlyContinue)
if ($drv) {
    "  всего сторонних пакетов: {0}" -f $drv.Count
    $drv | Sort-Object Date | Select-Object -First 60 |
        ForEach-Object { "    {0,-12} {1,-18} {2,-14} {3:dd.MM.yyyy} v{4}" -f $_.Driver, $_.ProviderName, $_.ClassName, $_.Date, $_.Version }
} else { '  Get-WindowsDriver недоступен' }

Hr 'Самая ранняя запись в журнале System и первые загрузки'
$first = Get-WinEvent -LogName System -Oldest -MaxEvents 1 -ErrorAction SilentlyContinue
if ($first) { "  журнал System начинается: {0:dd.MM.yyyy HH:mm:ss}" -f $first.TimeCreated }
$boots = @(Get-WinEvent -FilterHashtable @{LogName='System'; Id=12; ProviderName='Microsoft-Windows-Kernel-General'} -MaxEvents 200 -ErrorAction SilentlyContinue | Sort-Object TimeCreated)
if ($boots) {
    "  первая загрузка в журнале: {0:dd.MM.yyyy HH:mm:ss}" -f $boots[0].TimeCreated
    "  всего загрузок в журнале  : {0}" -f $boots.Count
}
