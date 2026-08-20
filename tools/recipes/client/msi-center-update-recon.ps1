$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Разведка перед обновлением MSI Center (СЗ 161190): чем именно обновлять и есть ли чем.
#
# Грабля: MSI Center — гибрид (классический инсталлятор в Program Files (x86) + UWP-пакет из
# Store + отдельные модули), поэтому «обновить» может значить три разных действия. Плюс
# апдейт требует интерактивной сессии и интернета на клиенте — оба факта надо знать ДО того,
# как тянуть 200 МБ инсталлятора через hub.
#
#   szcli exec <СЗ> -f tools\recipes\client\msi-center-update-recon.ps1

'== Классическая установка (Uninstall-ветка)'
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'MSI Center|Mystic|MSI SDK|Nahimic' } |
    ForEach-Object { "   {0,-40} v{1,-18} uninst: {2}" -f $_.DisplayName, $_.DisplayVersion, $_.UninstallString }

'== UWP-пакеты MSI (MSI Center UI живёт как Appx)'
Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'MSI|Micro-Star' } |
    ForEach-Object { "   {0,-42} {1,-16} {2}" -f $_.Name, $_.Version, $_.InstallLocation }

'== Исполняемые файлы MSI Center: версия и дата'
foreach ($p in @("${env:ProgramFiles(x86)}\MSI\MSI Center", "$env:ProgramFiles\MSI\MSI Center")) {
    if (-not (Test-Path $p)) { continue }
    Get-ChildItem $p -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'MSI.Center|CentralServer|LEDKeeper|Update' } |
        ForEach-Object { "   {0,-34} v{1,-16} {2:dd.MM.yyyy}" -f $_.Name, $_.VersionInfo.ProductVersion, $_.LastWriteTime }
}

'== Кто сейчас в системе (обновление требует живой сессии)'
try { (query user 2>&1) | ForEach-Object { "   $_" } } catch { "   query user недоступен" }
"   explorer: " + ((Get-Process explorer -ErrorAction SilentlyContinue | Measure-Object).Count) + " шт"

'== Интернет с клиента (нужен для скачивания апдейта самим MSI Center)'
foreach ($h in 'download.msi.com','www.msi.com','www.microsoft.com') {
    try {
        $r = Invoke-WebRequest "https://$h" -UseBasicParsing -TimeoutSec 8 -Method Head
        "   {0,-24} OK ({1})" -f $h, $r.StatusCode
    } catch { "   {0,-24} FAIL: {1}" -f $h, $_.Exception.Message.Split([char]10)[0] }
}

'== Store/UWP-канал обновления (если MSI Center из Store — обновится сам)'
$wu = Get-Service wuauserv,InstallService,ClipSVC -ErrorAction SilentlyContinue
$wu | ForEach-Object { "   {0,-16} {1} / {2}" -f $_.Name, $_.Status, $_.StartType }
