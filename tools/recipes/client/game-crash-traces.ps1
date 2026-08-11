$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Следы аварийного завершения ИГРЫ, когда в журнале Windows по ней ничего нет.
#
# Грабля (СЗ 160697, «вилітає в грі Enlisted»): APPCRASH по enlisted.exe в Application — ноль,
# WER — ноль. Значит игра не падала с исключением: её либо убивало вместе с машиной
# (hard-off), либо она выходила сама (device removed / anti-cheat). Различить это можно
# только по собственным логам игры: Gaijin пишет сессию в `.game_logs\<дата>__<pid>.clog`
# и ДОжимает файл при штатном выходе. Сессия, оборванная обрывом питания, остаётся
# несжатым `.log` или файлом, у которого LastWrite = момент смерти машины.
#
#   szcli exec <СЗ> -f tools\recipes\client\game-crash-traces.ps1
# Параметры (szcli exec не умеет аргументы — правятся здесь):
$GameDir = 'C:\Program Files (x86)\Games\Enlisted'
$Kp41Days = 40

'== Каталог игры'
if (-not (Test-Path $GameDir)) { "нет: $GameDir"; return }
"$GameDir"

'== Все логи сессий (.game_logs): имя = старт сессии, LastWrite = конец'
$lg = Join-Path $GameDir '.game_logs'
if (Test-Path $lg) {
    Get-ChildItem $lg -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object {
        $start = ''
        $m = [regex]::Match($_.Name, '(\d{4})_(\d{2})_(\d{2})_(\d{2})_(\d{2})_(\d{2})')
        if ($m.Success) {
            $start = "{0}.{1}.{2} {3}:{4}:{5}" -f $m.Groups[3].Value, $m.Groups[2].Value, $m.Groups[1].Value, $m.Groups[4].Value, $m.Groups[5].Value, $m.Groups[6].Value
            $st = [datetime]::ParseExact($m.Value, 'yyyy_MM_dd_HH_mm_ss', $null)
            $dur = [int]($_.LastWriteTime - $st).TotalMinutes
        }
        else { $dur = -1 }
        "   старт {0}  конец {1:dd.MM HH:mm:ss}  длит {2,4} мин  {3,7:N1} МБ  {4}" -f $start, $_.LastWriteTime, $dur, ($_.Length / 1MB), $_.Extension
    }
    $raw = @(Get-ChildItem $lg -File -Filter '*.log' -ErrorAction SilentlyContinue)
    "НЕсжатых .log (признак обрыва сессии): $($raw.Count)"
    $raw | ForEach-Object { "   {0:dd.MM.yyyy HH:mm:ss}  {1,7:N1} МБ  {2}" -f $_.LastWriteTime, ($_.Length / 1MB), $_.Name }
}
else { '   .game_logs нет' }

'== Дампы/креш-файлы в каталоге игры'
$cr = @(Get-ChildItem $GameDir -Recurse -Include '*.dmp', '*.mdmp', 'crash*', '*.crash' -ErrorAction SilentlyContinue)
if ($cr.Count) {
    $cr | Sort-Object LastWriteTime -Descending | Select-Object -First 20 |
        ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  {1,7:N2} МБ  {2}" -f $_.LastWriteTime, ($_.Length / 1MB), $_.FullName }
}
else { '   нет' }

'== Настройки/профиль игры (что за пресет и разрешение)'
foreach ($u in (Get-ChildItem (Join-Path $env:SystemDrive 'Users') -Directory -ErrorAction SilentlyContinue)) {
    foreach ($p in @('Documents\My Games\Enlisted', 'AppData\Local\enlisted', 'AppData\Local\Gaijin')) {
        $d = Join-Path $u.FullName $p
        if (Test-Path $d) {
            "   $d"
            Get-ChildItem $d -File -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending |
                Select-Object -First 8 | ForEach-Object { "      {0:dd.MM.yyyy HH:mm}  {1,7:N2} МБ  {2}" -f $_.LastWriteTime, ($_.Length / 1MB), $_.Name }
        }
    }
}

'== Наложение: сессии игры и аварийные выключения'
$since = (Get-Date).AddDays(-$Kp41Days)
Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 41; StartTime = $since } -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated | ForEach-Object {
        $t = $_.TimeCreated
        $near = @(Get-ChildItem $lg -File -ErrorAction SilentlyContinue |
            Where-Object { [math]::Abs(($_.LastWriteTime - $t).TotalMinutes) -le 30 })
        if ($near.Count) {
            "   KP41 {0:dd.MM HH:mm:ss} <== рядом лог сессии: {1}" -f $t, ($near.Name -join ', ')
        }
        else { "   KP41 {0:dd.MM HH:mm:ss} — логов игры в пределах 30 мин НЕТ (игра не работала)" -f $t }
    }
