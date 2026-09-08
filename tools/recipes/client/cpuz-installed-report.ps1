$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Снимок памяти/платы через CPU-Z, УЖЕ УСТАНОВЛЕННЫЙ у клиента (не пушим свой).
# Грабля (162003): у клиента стоит CPU-Z 2.19 — гонять свою копию незачем,
# но CLI-отчёт надо звать из своей рабочей папки и ждать файл (exe уходит в фон).
#   szcli exec <СЗ> -f tools\recipes\client\cpuz-installed-report.ps1

$exe = @('C:\Program Files\CPUID\CPU-Z\cpuz.exe','C:\Program Files (x86)\CPUID\CPU-Z\cpuz.exe') |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { 'CPU-Z не найден в Program Files'; exit 1 }

$out = Join-Path $env:TEMP ('cpuz-' + (Get-Date -Format 'HHmmss'))
Start-Process -FilePath $exe -ArgumentList "-txt=$out" -Wait -WindowStyle Hidden
$txt = "$out.txt"
for ($i = 0; $i -lt 30 -and -not (Test-Path $txt); $i++) { Start-Sleep -Seconds 1 }
if (-not (Test-Path $txt)) { 'CPU-Z: отчёт не появился'; exit 2 }

$lines = Get-Content $txt -Encoding UTF8
function Section($name, $count) {
    $i = ($lines | Select-String -SimpleMatch $name | Select-Object -First 1).LineNumber
    if ($i) { $lines | Select-Object -Skip ($i - 1) -First $count }
}
'== Memory SPD / профили'
Section 'Memory SPD' 90
'== Memory (текущий режим)'
Section 'Memory Information' 30
'== Mainboard / BIOS'
Section 'Mainboard Model' 20
'== напряжения и клоки'
$lines | Where-Object { $_ -match 'VDDIO|VDD |VSOC|VDDQ|CPU VDDCR|Core Voltage|AGESA|SMU|Uncore|FCLK|UCLK|MCLK' } | Select-Object -First 40
"(полный отчёт на клиенте: $txt)"
