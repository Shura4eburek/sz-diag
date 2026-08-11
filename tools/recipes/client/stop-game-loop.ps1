$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Снять игровой цикл: задача + сама игра + процесс-обёртка. Порядок важен — сначала задача,
# иначе она переподнимет игру на следующей итерации (СЗ 161346).
# Парный к start-game-loop.ps1.
$Sz   = '000000'   # ← номер СЗ
$Task = "szdiag-game-$Sz"

('задача: ' + (schtasks /end /tn $Task 2>&1))
('удаление: ' + (schtasks /delete /tn $Task /f 2>&1))

# обёртка: powershell, запущенный с game-loop.ps1 в командной строке
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*game-loop.ps1*' } |
    ForEach-Object { ('обёртка pid ' + $_.ProcessId + ' — снята'); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

$g = Get-Process Cyberpunk2077 -ErrorAction SilentlyContinue
if ($g) { $g | ForEach-Object { ('игра pid ' + $_.Id + ' — снята'); Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }
else { 'игра уже не запущена' }

Start-Sleep -Seconds 5
$still = Get-Process Cyberpunk2077 -ErrorAction SilentlyContinue
('после остановки: ' + $(if ($still) { 'игра ВСЁ ЕЩЁ жива — разбираться руками' } else { 'чисто' }))

$log = Get-ChildItem 'C:\ProgramData\szdiag' -Filter 'game-loop-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    ('лог: ' + $log.FullName)
    $lines = @(Get-Content $log.FullName -Encoding UTF8)
    ('итераций записано: ' + (@($lines | Where-Object { $_ -match 'завершена за' }).Count))
    $lines | Select-Object -Last 6 | ForEach-Object { '   ' + $_ }
}
