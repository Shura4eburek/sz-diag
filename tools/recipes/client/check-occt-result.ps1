$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Итог прогона OCCT приборно: сколько РЕАЛЬНО шёл тест и были ли ошибки.
#
# Грабли, которые это породили (СЗ 161716, 24.08.2026):
#  - наблюдатель сенсоров под полной нагрузкой пишет 4 строки за 20 минут (бэклог п.206),
#    поэтому «сколько держалась нагрузка» из CSV взять нельзя — единственный честный
#    источник длительности и ошибок остаётся лог самого OCCT;
#  - «задача запущена» и «тест шёл час» — разные утверждения: на протухшей лицензии
#    OCCTCmd выходит сразу, а задача рапортует Last Result 0 (161190);
#  - результаты лежат в Documents ЗАЛОГИНЕННОГО пользователя, а exec работает под SYSTEM —
#    искать надо по владельцу explorer.exe, иначе папка «не найдена» при живых результатах.
#
#   szcli exec <СЗ> -f tools\recipes\client\check-occt-result.ps1

$HoursBack = 6                        # какой давности прогоны показывать
$TaskName  = 'szdiag-occtcomb-161716' # задача прогона (под свою СЗ)

# Профиль залогиненного: под SYSTEM $env:USERPROFILE указывает в systemprofile.
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
$roots = @()
if ($expl) {
    $owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
    $userHome = Join-Path 'C:\Users' $owner.User
    $roots += (Join-Path $userHome 'Documents\OCCT')
    $roots += (Join-Path $userHome 'OneDrive\Documents\OCCT')   # Documents бывает перенаправлен в OneDrive
    "пользователь: $($owner.Domain)\$($owner.User)"
}
$roots += 'C:\OCCT'
$roots = @($roots | Where-Object { Test-Path $_ } | Select-Object -Unique)
if (-not $roots) { 'папок с результатами OCCT не найдено'; return }

$since = (Get-Date).AddHours(-$HoursBack)
foreach ($root in $roots) {
    "== $root"
    $files = @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $since } | Sort-Object LastWriteTime)
    if (-not $files) { '   свежих файлов нет'; continue }
    foreach ($f in $files) {
        '   {0,-55} {1,8} б  {2:HH:mm:ss}' -f $f.FullName.Substring($root.Length + 1), $f.Length, $f.LastWriteTime
    }

    # Длительность считаем по разбросу времён файлов: первый записанный — старт, последний — финиш.
    # Это честнее, чем «задача запущена в HH:MM»: старт задачи не равен старту нагрузки.
    $first = $files[0]; $last = $files[-1]
    '   окно записи: {0:HH:mm:ss} -> {1:HH:mm:ss} ({2:n1} мин)' -f $first.LastWriteTime, $last.LastWriteTime,
        ($last.LastWriteTime - $first.LastWriteTime).TotalMinutes

    foreach ($f in $files) {
        if ($f.Extension -eq '.txt' -or $f.Extension -eq '.log') {
            "   --- $($f.Name)"
            Get-Content $f.FullName -Tail 40 | ForEach-Object { '      ' + $_ }
        }
        elseif ($f.Extension -eq '.csv') {
            $n = (Get-Content $f.FullName | Measure-Object -Line).Lines
            "   --- $($f.Name): $n строк"
            Get-Content $f.FullName -Tail 3 | ForEach-Object { '      ' + $_ }
        }
    }

    $txt = @($files | Where-Object { $_.Extension -eq '.txt' -or $_.Extension -eq '.log' -or $_.Extension -eq '.csv' })
    if ($txt) {
        $hits = @($txt | Select-String -Pattern 'error|fail|ошибк' -ErrorAction SilentlyContinue)
        if ($hits) {
            '   !!! строки с ошибками:'
            $hits | Select-Object -First 30 | ForEach-Object { '      ' + $_.Filename + ': ' + $_.Line.Trim() }
        }
        else { '   ошибок в текстовых файлах не найдено' }
    }
}

'== процессы OCCT сейчас'
$p = @(Get-Process OCCTCmd, OCCT -ErrorAction SilentlyContinue)
if ($p) { $p | ForEach-Object { "   $($_.ProcessName) pid=$($_.Id) cpu=$([int]$_.CPU)с" } }
else { '   не запущены — тест завершён или не стартовал' }

"== задача $TaskName"
schtasks /query /tn $TaskName /fo list /v 2>&1 |
    Select-String 'Status|Result|Start Time|Состояние|результат' |
    ForEach-Object { '   ' + $_.Line.Trim() }
