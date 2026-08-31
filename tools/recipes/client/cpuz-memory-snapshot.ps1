$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Снимок настроек памяти/платы через CPU-Z CLI.
# Грабля (161538): по WMI видно только ConfiguredClockSpeed (6000) — ни таймингов,
# ни напряжений (VDDIO/VDD/VSOC), ни версии AGESA. Без них не отличить «EXPO как есть»
# от «крутили руками» и не понять, во что упирается IMC.
# Вторая грабля: $PSScriptRoot внутри szcli exec пустой (скрипт едет текстом, не файлом) —
# путь к тулзе искать перебором известных мест, а не относительно скрипта.

$candidates = @(
    'C:\Users\User\Desktop\Client-test\tools\cpuz',
    'C:\szdiag\tools\cpuz'
)
$toolDir = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $toolDir) {
    $agent = Get-Process -Name 'agent' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($agent) {
        $p = Join-Path (Split-Path $agent.Path) 'tools\cpuz'
        if (Test-Path $p) { $toolDir = $p }
    }
}
if (-not $toolDir) { Write-Output 'CPU-Z: папка не найдена'; exit 1 }

$exe = Get-ChildItem -Path $toolDir -Filter '*.exe' |
    Where-Object { $_.Name -notmatch 'x32' } | Select-Object -First 1
if (-not $exe) { Write-Output "CPU-Z: exe не найден в $toolDir"; exit 1 }
Write-Output "EXE: $($exe.FullName)"

$out = Join-Path $env:TEMP 'cpuz-report'
Remove-Item "$out.txt" -Force -ErrorAction SilentlyContinue

Start-Process -FilePath $exe.FullName -ArgumentList "-txt=$out" -WindowStyle Hidden
$stable = 0
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 1
    if (-not (Test-Path "$out.txt")) { continue }
    $len = (Get-Item "$out.txt").Length
    Start-Sleep -Seconds 1
    if ($len -gt 1000 -and (Get-Item "$out.txt").Length -eq $len) { $stable++ }
    if ($stable -ge 2) { break }
}
if (-not (Test-Path "$out.txt")) { Write-Output 'CPU-Z не отдал отчёт за 90 с'; exit 1 }

# Из отчёта интересны только эти блоки — целиком он на сотни килобайт
$text = Get-Content "$out.txt" -Raw
foreach ($block in 'DMI BIOS', 'DMI Baseboard', 'Memory SPD', 'Memory Frequency', 'Timings Table') {
    $idx = $text.IndexOf($block)
    if ($idx -ge 0) {
        Write-Output "===== $block ====="
        Write-Output $text.Substring($idx, [Math]::Min(2200, $text.Length - $idx))
        Write-Output ''
    }
}
Write-Output "FULL_REPORT: $out.txt"
