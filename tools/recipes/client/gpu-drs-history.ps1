$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Когда в панели NVIDIA меняли настройки: даты файлов базы профилей драйвера (СЗ 161190).
#
# Грабля: на 161190 глобальный режим питания оказался «Максимальная производительность» —
# карта из P0 не выходит вообще, при этом 17.08 она в P8 уходила, то есть настройку выставили
# позже. Сам nvdrsdb0.bin переписывается при каждом старте драйвера и своей датой ничего не
# доказывает; предыдущая копия nvdrsdb1.bin хранит дату ПРЕДЫДУЩЕЙ записи — по ней и видно,
# когда база менялась в прошлый раз.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-drs-history.ps1

$drs = Join-Path $env:ProgramData 'NVIDIA Corporation\Drs'
Get-ChildItem $drs -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    ForEach-Object { '   {0,-16} {1,12:N0} б   создан {2:dd.MM.yyyy HH:mm}  изменён {3:dd.MM.yyyy HH:mm}' -f $_.Name, $_.Length, $_.CreationTime, $_.LastWriteTime }

'== события установки/обновления драйвера и панели за 30 дней'
$since = (Get-Date).AddDays(-30)
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$since} -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match 'NVIDIA|MsiInstaller' -and $_.Message -match 'NVIDIA|Control Panel' } |
    Select-Object -First 15 |
    ForEach-Object { '   {0:dd.MM HH:mm}  {1}  {2}' -f $_.TimeCreated, $_.ProviderName, ($_.Message -split "`n")[0] }

'== процессы панели/сервисов NVIDIA сейчас'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'nv|NVIDIA' } |
    ForEach-Object { '   {0,-24} pid {1,-7} старт {2:dd.MM HH:mm}' -f $_.ProcessName, $_.Id, $_.StartTime }
