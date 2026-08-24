$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# НАГРУЗКА НА СЕТЬ: клиент непрерывно тянет большой файл с шары бокса, скорость меряется
# по окнам. Пара к net-watch.ps1 — вахта без трафика бесполезна.
#
# Грабля (СЗ 162367, ПК-клуб с загрузкой по PXE): жалоба «відвалюється мережа», а отвал на
# простое не воспроизводится — дефект вылазит на трафике и на нагреве карты. Ручное
# «скопируй файлик» не годится: нужен ЦИКЛ на часы, лог с flush (обрыв рвёт сессию, и всё
# несохранённое теряется) и скорость по окнам, потому что важна не средняя за прогон, а
# ПРОСАДКА в конкретную секунду — она и есть отвал, который пользователь видит как «не
# стартує».
#
# Читает в никуда: на диск клиента ничего не пишется, данные клиента не трогаются.
# Монтирование шары снимается в конце и при обрыве — доступ временный и без следов.
#
#   szcli exec <СЗ> -f tools\recipes\client\net-load.ps1 --detach
# Запускать ПОСЛЕ net-watch.ps1, чтобы вахта видела свой трафик.

$Hub      = '192.168.94.123'   # ← бокс: чья шара
$Share    = 'Share'
$User     = '1'                # ← креды раздачи (в гите их нет, см. память)
$Pass     = '1'
$Minutes  = 60                 # ← сколько качать
$WindowSec = 5                 # окно замера скорости
$MinFileMB = 200               # мельче не берём: на коротком файле скорость не устаканивается
$SlowPct  = 40                 # окно медленнее этого % от медианы = просадка, пишем в лог

$LogDir = 'C:\ProgramData\szdiag'
New-Item -ItemType Directory -Path $LogDir -Force -ErrorAction SilentlyContinue | Out-Null
$Log = Join-Path $LogDir ("net-load-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true
function Say([string]$m) { $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $line; $sw.WriteLine($line) }

$unc = "\\$Hub\$Share"
$mounted = $false

# Гостевой SMB на Win10/11 закрыт — монтируем только с явными кредами, иначе Access denied.
Say "монтирую $unc"
$r = net use $unc $Pass /user:$User 2>&1
Say ("  " + ($r -join ' '))
if ($LASTEXITCODE -ne 0) {
    Say 'ШАРА НЕ ПОДНЯЛАСЬ — нагрузки не будет. Проверь фаервол/VPN на боксе (error 67/53).'
    $sw.Close(); return
}
$mounted = $true

try {
    $src = @(Get-ChildItem $unc -Recurse -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Length -gt ($MinFileMB * 1MB) } |
             Sort-Object Length -Descending | Select-Object -First 1)
    if (-not $src.Count) { Say "на шаре нет файлов крупнее $MinFileMB МБ — качать нечего"; return }
    $file = $src[0]
    Say ("качаю: " + $file.FullName + "  ({0:N0} МБ)" -f ($file.Length / 1MB))
    Say "план $Minutes мин, окно замера $WindowSec с"

    $buf = New-Object byte[] (1MB)
    $deadline = (Get-Date).AddMinutes($Minutes)
    $winStart = Get-Date; $winBytes = 0
    $rates = @(); $passes = 0; $errors = 0; $totalMB = 0
    $slowWins = 0; $stallWins = 0

    while ((Get-Date) -lt $deadline) {
        try {
            $fs = [IO.File]::OpenRead($file.FullName)
            try {
                while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                    $winBytes += $n; $totalMB += $n / 1MB
                    $el = ((Get-Date) - $winStart).TotalSeconds
                    if ($el -ge $WindowSec) {
                        $mbs = ($winBytes / 1MB) / $el
                        $rates += $mbs
                        # Медиана, а не среднее: одна просадка в ноль утягивает среднее и
                        # делает порог бессмысленным именно там, где дефект.
                        $med = ($rates | Sort-Object)[[int]($rates.Count / 2)]
                        if ($mbs -lt 0.1) {
                            $stallWins++
                            Say ("СТОП передачи: {0:N2} МБ/с за {1:N0} с (медиана {2:N1})" -f $mbs, $el, $med)
                        } elseif ($rates.Count -gt 5 -and $mbs -lt ($med * $SlowPct / 100)) {
                            $slowWins++
                            Say ("ПРОСАДКА: {0:N1} МБ/с ({1:N0} % от медианы {2:N1})" -f $mbs, ($mbs / $med * 100), $med)
                        }
                        $winStart = Get-Date; $winBytes = 0
                    }
                    if ((Get-Date) -ge $deadline) { break }
                }
            } finally { $fs.Dispose() }
            $passes++
        } catch {
            # Обрыв SMB-сессии — это и есть «сеть отвалилась», ловим как событие, а не как
            # фатальную ошибку: дефект может быть плавающим, прогон должен продолжаться.
            $errors++
            Say ("ОБРЫВ ЧТЕНИЯ #{0}: {1}" -f $errors, $_.Exception.Message)
            Start-Sleep -Seconds 3
        }
    }

    Say '--- ИТОГ ---'
    Say ("прокачано      : {0:N1} ГБ за {1} проходов файла" -f ($totalMB / 1024), $passes)
    if ($rates.Count) {
        $s = $rates | Measure-Object -Minimum -Maximum -Average
        $med = ($rates | Sort-Object)[[int]($rates.Count / 2)]
        Say ("скорость МБ/с  : медиана {0:N1}, среднее {1:N1}, мин {2:N2}, макс {3:N1}" -f $med, $s.Average, $s.Minimum, $s.Maximum)
        Say ("окон замера    : {0}, просадок {1}, полных стопов {2}" -f $rates.Count, $slowWins, $stallWins)
    }
    Say ("обрывов сессии : $errors")
    if ($errors -eq 0 -and $stallWins -eq 0 -and $slowWins -eq 0) {
        Say 'сеть держала нагрузку ровно — отвал на этом отрезке не воспроизведён'
    }
} finally {
    # Откат обязателен даже при падении: смонтированная шара — это след на клиенте.
    if ($mounted) {
        $d = net use $unc /delete /y 2>&1
        Say ("размонтирую: " + ($d -join ' '))
    }
    Say "лог: $Log"
    $sw.Close()
}
