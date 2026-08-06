$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Подготовка машины к приборному прогону. Две молчаливые грабли, каждая стоила времени:
#   1) lhmmon пишет в захардкоженный C:\OCCT\sensors.csv — без папки падает и НИЧЕГО не пишет (п.38);
#   2) Defender режет kernel-драйвер WinRing0 как PUA — CSV выглядит рабочим, но CPU-колонки нули.
# Обе ветки — с парным откатом в cleanup-stress.ps1 (модель угроз: следов не оставляем).
#   szcli exec <СЗ> -f tools\recipes\client\prep-stress.ps1

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent

if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null; 'C:\OCCT создана' }
else { 'C:\OCCT уже есть (прошлый sensors.csv затрётся — забери его заранее)' }

try {
    Add-MpPreference -ExclusionPath (Join-Path $base 'tools\lhmmon') -ErrorAction Stop
    'Defender: исключение на папку lhmmon добавлено'
} catch { "Defender: не вышло — $($_.Exception.Message)" }

$mp = Get-MpComputerStatus -ErrorAction SilentlyContinue
if ($mp) { "Defender realtime: $($mp.RealTimeProtectionEnabled)" }
$dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue
if ($dg) {
    "HVCI running: $($dg.SecurityServicesRunning -join ',')"
    if ($dg.SecurityServicesRunning -contains 2) {
        '⚠ HVCI включён — драйвер R0lhmmon может не подняться. После старта сенсоров ОБЯЗАТЕЛЬНО'
        '  проверить, что Package Power и Tctl/Tdie не нули: ноль здесь читается как «холодный CPU».'
    }
}
