# Разведка по GPU-ветке: TDR/LiveKernelEvent за последние дни + текущее состояние видеокарты.
# Грабля (СЗ 161211, 20.08.2026): секция `livekernel` в diag даёт историю за всё время, но
# не отвечает на вопрос «сыпется ли ОНО прямо сейчас, пока машина у нас». Плюс diag не снимает
# мгновенные показания GPU (температура/потребление/ширина линии PCIe) — а для 5090 с водянкой
# это первое, на что смотришь.
# Запуск: szcli exec <СЗ> -f tools\recipes\client\gpu-tdr-log-recon.ps1 --timeout 180
$since = (Get-Date).AddDays(-3)

"=== LiveKernelEvent (Application Id=1001) за 3 дня, по часам ==="
Get-WinEvent -FilterHashtable @{LogName='Application';Id=1001;StartTime=$since} -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -match 'Windows Error Reporting' -and $_.Message -match 'LiveKernelEvent' } |
  ForEach-Object { [pscustomobject]@{ T = $_.TimeCreated.ToString('MM-dd HH'); Code = $_.Properties[2].Value } } |
  Group-Object T, Code | Sort-Object Name | Select-Object -Last 30 |
  ForEach-Object { "{0}  x{1}" -f $_.Name, $_.Count }

"=== Display / nvlddmkm / amdkmdag в System за 3 дня (первые 25) ==="
Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$since} -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -match 'Display|nvlddmkm|amdkmdag' } |
  Select-Object -First 25 TimeCreated, Id, ProviderName,
    @{n='M';e={ $_.Message.Substring(0, [Math]::Min(110, $_.Message.Length)) }} | Format-List

"=== Падения приложений (Application Id=1000) за 3 дня — чем была занята машина ==="
Get-WinEvent -FilterHashtable @{LogName='Application';Id=1000;StartTime=$since} -ErrorAction SilentlyContinue |
  Select-Object -First 10 TimeCreated, @{n='M';e={ ($_.Message -split "`n")[0] }} | Format-List

"=== Uptime / boot ==="
(Get-CimInstance Win32_OperatingSystem).LastBootUpTime

"=== GPU сейчас (nvidia-smi) ==="
# nvidia-smi живёт то в NVSMI, то в System32 — проверяем оба места, иначе молча «нет данных».
$smi = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
if (-not (Test-Path $smi)) { $smi = "$env:SystemRoot\System32\nvidia-smi.exe" }
if (Test-Path $smi) {
    & $smi --query-gpu=name,driver_version,temperature.gpu,temperature.memory,power.draw,power.limit,clocks.sm,utilization.gpu,pcie.link.gen.current,pcie.link.width.current --format=csv
} else { "nvidia-smi не найден" }
