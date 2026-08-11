$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто именно падает и с каким кодом: Application 1000/1001 за ВСЮ историю журнала
# + разбор WER-отчётов (ReportArchive/ReportQueue), где лежит exception code и виновный модуль.
#
# Грабля (СЗ 160697, «вилітає в грі Enlisted»): секция событий в diag режет Application
# до 3 дней и 40 строк — краш игры двухнедельной давности там просто не виден, и по diag
# выходит «аварий приложений нет». Плюс сообщения журнала локализованы (клиентская винда
# украинская), поэтому поля берём из Properties (язык не влияет), а не regex'ом по тексту.
#
#   szcli exec <СЗ> -f tools\recipes\client\app-crash-detail.ps1
# Параметр (szcli exec не умеет аргументы, param() ломает запуск — правится здесь):
$AppFilter = 'enlisted'

function Fld($e, $i) {
    if ($e.Properties -and $e.Properties.Count -gt $i) { [string]$e.Properties[$i].Value } else { '' }
}

'== Application Id=1000 (падения приложений), вся история'
$ev = @(Get-WinEvent -FilterHashtable @{LogName = 'Application'; Id = 1000 } -ErrorAction SilentlyContinue)
"TOTAL: $($ev.Count)"
if ($ev.Count) {
    $rows = foreach ($e in $ev) {
        [pscustomobject]@{
            When = $e.TimeCreated
            App  = (Fld $e 0)
            Ver  = (Fld $e 1)
            Mod  = (Fld $e 3)
            Code = (Fld $e 6)
            Off  = (Fld $e 7)
            Path = (Fld $e 10)
        }
    }
    '-- по приложениям'
    $rows | Group-Object App | Sort-Object Count -Descending | ForEach-Object {
        $s = $_.Group | Sort-Object When
        "   {0,-30} {1,4}  {2:dd.MM HH:mm} .. {3:dd.MM HH:mm}" -f $_.Name, $_.Count, $s[0].When, $s[-1].When
    }
    '-- последние 25 записей'
    $rows | Sort-Object When -Descending | Select-Object -First 25 | ForEach-Object {
        "   [{0:dd.MM.yyyy HH:mm:ss}] {1} v{2} <- mod={3} code={4} off={5}" -f $_.When, $_.App, $_.Ver, $_.Mod, $_.Code, $_.Off
    }
    "-- совпадения с фильтром '$AppFilter'"
    $hit = @($rows | Where-Object { $_.App -match $AppFilter -or $_.Mod -match $AppFilter -or $_.Path -match $AppFilter })
    if ($hit.Count) {
        $hit | Sort-Object When -Descending | ForEach-Object {
            "   [{0:dd.MM.yyyy HH:mm:ss}] {1} <- mod={2} code={3} off={4}" -f $_.When, $_.App, $_.Mod, $_.Code, $_.Off
            "      path={0}" -f $_.Path
        }
    }
    else { '   нет' }
}

'== Application Id=1001 (WER), совпадения с фильтром'
$w = @(Get-WinEvent -FilterHashtable @{LogName = 'Application'; Id = 1001 } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match $AppFilter })
"TOTAL совпадений: $($w.Count)"
$w | Sort-Object TimeCreated -Descending | Select-Object -First 8 | ForEach-Object {
    "   [{0:dd.MM.yyyy HH:mm:ss}]" -f $_.TimeCreated
    ($_.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 14) | ForEach-Object { "      $_" }
}

'== WER: отчёты на диске (ReportArchive / ReportQueue)'
$roots = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue')
)
foreach ($u in (Get-ChildItem (Join-Path $env:SystemDrive 'Users') -Directory -ErrorAction SilentlyContinue)) {
    $roots += (Join-Path $u.FullName 'AppData\Local\Microsoft\Windows\WER\ReportArchive')
    $roots += (Join-Path $u.FullName 'AppData\Local\Microsoft\Windows\WER\ReportQueue')
}
$wer = @()
foreach ($r in $roots) {
    if (Test-Path $r) {
        $wer += @(Get-ChildItem $r -Recurse -Filter 'Report.wer' -ErrorAction SilentlyContinue)
    }
}
"TOTAL Report.wer: $($wer.Count)"
$parsed = foreach ($f in $wer) {
    $txt = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $txt) { continue }
    $h = @{}
    foreach ($line in ($txt -split "`r?`n")) {
        $i = $line.IndexOf('=')
        if ($i -gt 0) { $h[$line.Substring(0, $i)] = $line.Substring($i + 1) }
    }
    [pscustomobject]@{
        When = $f.LastWriteTime
        Type = $h['EventType']
        App  = $h['AppName']
        Sig1 = $h['Sig[0].Value']
        Sig4 = $h['Sig[3].Value']
        Sig5 = $h['Sig[4].Value']
        Sig7 = $h['Sig[6].Value']
        Path = $h['AppPath']
        File = $f.FullName
    }
}
'-- по типу события'
$parsed | Group-Object Type | Sort-Object Count -Descending | ForEach-Object {
    "   {0,-32} {1,4}" -f $_.Name, $_.Count
}
'-- последние 20'
$parsed | Sort-Object When -Descending | Select-Object -First 20 | ForEach-Object {
    "   [{0:dd.MM.yyyy HH:mm}] {1} | {2} | sig: {3} / {4} / {5} / {6}" -f $_.When, $_.Type, $_.Sig1, $_.Sig4, $_.Sig5, $_.Sig7, ''
}
"-- WER по фильтру '$AppFilter'"
$wf = @($parsed | Where-Object { $_.App -match $AppFilter -or $_.Sig1 -match $AppFilter -or $_.Path -match $AppFilter })
if ($wf.Count) {
    $wf | Sort-Object When -Descending | Select-Object -First 15 | ForEach-Object {
        "   [{0:dd.MM.yyyy HH:mm}] {1}" -f $_.When, $_.Type
        "      app={0} mod={1} code={2}" -f $_.Sig1, $_.Sig5, $_.Sig7
        "      {0}" -f $_.File
    }
}
else { '   нет' }

'== Игра на диске (где лежит, когда запускалась)'
$found = @()
foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3').DeviceID) {
    $found += @(Get-ChildItem "$d\" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'Enlisted|Steam|Games|Ігри|Игры' } |
        ForEach-Object { "   {0}  (изменена {1:dd.MM.yyyy HH:mm})" -f $_.FullName, $_.LastWriteTime })
}
if ($found.Count) { $found } else { '   каталогов с играми в корнях дисков нет' }
