$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Скільки прожив кожен сеанс, що закінчився вимкноном — по офлайн-журналу з WinPE (СЗ 161556).
#
# Грабля: `szcli reboots` працює з живим агентом, а коли машина в PE (ОС не пускає, лаунчер клубу,
# немає адмінки) — рахувати немає чим. Kernel-Power 41 пишеться ПІСЛЯ завантаження і описує
# ПОПЕРЕДНІЙ сеанс, тому дата події ≠ дата відмови: беремо передостанній Kernel-General 12.
#
# Друкує: скільки стартів / чистих завершень / вимкнонів і таблицю «сеанс стартував → прожив N хв».
# param не використовуємо: `szcli exec -f` його не переварює (бэклог п.189).

$Sys = ''
foreach ($l in [char[]]'CDEFGHIJ') {
    if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
}
$log = "$Sys\Windows\System32\winevt\Logs\System.evtx"
if (-not (Test-Path $log)) { "System.evtx не знайдено ($log)"; exit 1 }

$ev = Get-WinEvent -Path $log -ErrorAction SilentlyContinue
"журнал: $log"
"діапазон: $(($ev | Select-Object -Last 1).TimeCreated) .. $(($ev | Select-Object -First 1).TimeCreated)"

$k41 = ($ev | Where-Object { $_.Id -eq 41  -and $_.ProviderName -match 'Kernel-Power'   }).TimeCreated | Sort-Object
$b12 = ($ev | Where-Object { $_.Id -eq 12  -and $_.ProviderName -match 'Kernel-General' }).TimeCreated | Sort-Object
$b13 = ($ev | Where-Object { $_.Id -eq 13  -and $_.ProviderName -match 'Kernel-General' }).TimeCreated | Sort-Object
'стартів ОС: {0}, чистих завершень: {1}, жорстких вимкнонів: {2}' -f $b12.Count, $b13.Count, $k41.Count

'--- вимкнони: коли впав сеанс і скільки прожив ---'
foreach ($k in $k41) {
    # 41 пишеться при завантаженні ПІСЛЯ падіння: старт упалого сеансу — передостанній 12 перед ним
    $prev = $b12 | Where-Object { $_ -lt $k } | Select-Object -Last 2 | Select-Object -First 1
    if ($prev) {
        '{0:yyyy-MM-dd HH:mm} вимкнон; сеанс стартував {1:yyyy-MM-dd HH:mm}, прожив {2} хв' -f
            $k, $prev, [math]::Round(($k - $prev).TotalMinutes)
    } else {
        '{0:yyyy-MM-dd HH:mm} вимкнон; старт сеансу поза журналом' -f $k
    }
}

'--- вимкнони по місяцях ---'
$k41 | Group-Object { '{0:yyyy-MM}' -f $_ } | Sort-Object Name |
    ForEach-Object { '{0}: {1}' -f $_.Name, $_.Count }
