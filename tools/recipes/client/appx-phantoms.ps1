# Фантомные Appx: пакет зарегистрирован, а файлов на диске нет.
# Грабля (123123): после подмены куста реестра «не запускался Калькулятор», и штатный
# рецепт из интернета — Get-AppxPackage -AllUsers | Add-AppxPackage -Register
# "$($_.InstallLocation)\AppXManifest.xml" — сыпал 0x80070003 «не найден путь».
# Причина: InstallLocation ПУСТОЙ, потому что каталоги пакетов физически удалены
# (93 из 159). Перерегистрировать нечего — лечится только возвратом файлов
# (in-place repair / install.wim), а не повторным Register.
# Скрипт отвечает на вопрос «регистрация битая или файлов нет» ДО попыток чинить.
$all = Get-AppxPackage -AllUsers
$present = @()
$phantom = @()
foreach ($p in $all) {
    if ($p.InstallLocation -and (Test-Path $p.InstallLocation)) { $present += $p } else { $phantom += $p }
}
('всего пакетов:      ' + $all.Count)
('файлы на месте:     ' + $present.Count)
('фантомы (нет файлов): ' + $phantom.Count)
''
'--- фантомы ---'
$phantom | Select-Object -ExpandProperty Name | Sort-Object | Out-String
'--- каталоги пакетов ---'
foreach ($root in @('C:\Program Files\WindowsApps', 'E:\WindowsApps', 'D:\WindowsApps')) {
    if (Test-Path $root) {
        $cnt = (Get-ChildItem $root -Directory -Force -ErrorAction SilentlyContinue).Count
        ($root + ' : ' + $cnt + ' папок')
    }
}
'--- манифесты в AppRepository (записи живы, даже когда файлов нет) ---'
('xml: ' + (Get-ChildItem 'C:\ProgramData\Microsoft\Windows\AppRepository' -Filter '*.xml' -ErrorAction SilentlyContinue).Count)
