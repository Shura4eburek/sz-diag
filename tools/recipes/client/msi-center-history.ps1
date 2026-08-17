$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# История вендорского софта MSI (MSI Center / Mystic Light / LEDKeeper2): версии, даты установки
# и обновлений, даты файлов, автозапуск.
#
# Грабля (СЗ 161190): доказано, что LEDKeeper2 (подсветка Mystic Light) держит видеокарту в P0,
# из-за чего вентилятор постоянно молотит на ~70 % — это и есть жалоба клиента. Дальше нужен
# ответ «с какого числа это началось», чтобы сверить с журналом: livekernel-события 0x1CC
# идут ровным фоном с 05.04.2026, TDR 0x117 — с 27.07.2026. Если даты установки/обновления
# софта ложатся на эти рубежи, одна причина закрывает всю картину, а не только шум вентилятора.
#
#   szcli exec <СЗ> -f tools\recipes\client\msi-center-history.ps1

'== Установленные продукты MSI'
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'MSI|Mystic|Dragon|Nahimic' } |
    ForEach-Object { "   {0,-42} v{1,-16} установлен {2}" -f $_.DisplayName, $_.DisplayVersion, $_.InstallDate }

'== Файлы MSI Center: даты (когда реально обновлялось)'
$roots = @("$env:ProgramFiles(x86)\MSI\MSI Center", "$env:ProgramFiles\MSI\MSI Center")
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    "   $r"
    Get-ChildItem $r -Recurse -File -Include '*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
        ForEach-Object { "      {0:dd.MM.yyyy HH:mm}  {1}" -f $_.LastWriteTime, $_.Name }
}

'== Установки/удаления из журнала (MsiInstaller), всё что про MSI-софт'
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'MsiInstaller' } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'MSI Center|Mystic|Nahimic|SDK' } |
    Sort-Object TimeCreated -Descending | Select-Object -First 20 |
    ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  {1}" -f $_.TimeCreated, ($_.Message -split "`n")[0] }

'== Службы MSI: когда стартовали и откуда'
Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'MSI|Mystic' } |
    ForEach-Object { "   {0,-26} {1,-9} {2}" -f $_.Name, $_.State, $_.PathName }

'== Задачи планировщика MSI'
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match 'MSI|Mystic|LED' } |
    ForEach-Object {
        $i = $_ | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
        "   {0,-34} {1,-9} last {2}" -f $_.TaskName, $_.State, $i.LastRunTime
    }
