$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Где на этой машине реально лежат инструменты. Породила СЗ 161716: агент был запущен из
# C:\Users\<usr>\OneDrive\Desktop\Client-test, поэтому ToolsDirectory.Resolve увёл раздачу
# в C:\ProgramData\szdiag\tools — а тест-раннер и все рецепты искали tools\ рядом с агентом.
# Проявляется это не ошибкой «не найдено», а строкой «процесс НЕ ЗАПУСТИЛСЯ» (бэклог п.151).
# Гонять ПЕРЕД любым прогоном стресса на незнакомой машине.
#   szcli exec <СЗ> -f tools\recipes\client\where-tools.ps1

$p = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $p) { throw 'агент не найден' }
$base = Split-Path $p.ExecutablePath -Parent
"агент: $($p.ExecutablePath)"
$cloud = $base -match 'OneDrive|Dropbox|Google Drive|Яндекс'
"облачная папка: $cloud" + $(if ($cloud) { '  → инструменты уехали в ProgramData' } else { '' })

foreach ($dir in @("$base\tools", 'C:\ProgramData\szdiag\tools')) {
    "--- $dir ---"
    if (Test-Path $dir) {
        Get-ChildItem $dir -Directory | ForEach-Object {
            $n = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum)
            "  {0,-12} {1,4} файлов  {2,7:N1} МБ" -f $_.Name, $n.Count, ($n.Sum / 1MB)
        }
    } else { '  (нет)' }
}

'--- ключевые exe ---'
$want = @{
    'occt'      = 'OCCTCmd.exe'
    'lhmmon'    = 'lhmmon.exe'
    'ycruncher' = 'y-cruncher.exe'
    'prime95'   = 'prime95.exe'
    'tm5'       = 'TM5.exe'
    'furmark'   = 'furmark.exe'
}
foreach ($t in $want.Keys | Sort-Object) {
    $hit = @("$base\tools\$t\$($want[$t])", "C:\ProgramData\szdiag\tools\$t\$($want[$t])") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    "  {0,-10} {1}" -f $t, $(if ($hit) { $hit } else { '— не доставлен' })
}

# Лицензия OCCT протухает молча: тест «не запускается» вместо внятной ошибки (бэклог п.152)
$oke = @("$base\tools\occt", 'C:\ProgramData\szdiag\tools\occt') |
    Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Filter '*.oke' -ErrorAction SilentlyContinue }
if ($oke) {
    '--- лицензия OCCT ---'
    foreach ($f in $oke) {
        $head = (Get-Content $f.FullName -Raw).Split('|')[0]
        try {
            $txt = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($head))
            $till = [datetime]::ParseExact($txt.Split(';')[2], 'yyyy/MM/dd', $null)
            $days = ($till - (Get-Date).Date).Days
            "  {0}: до {1:dd.MM.yyyy} ({2})" -f $f.Name, $till, $(if ($days -lt 0) { "ПРОТУХЛА $([math]::Abs($days)) дн. назад" } elseif ($days -eq 0) { 'истекает СЕГОДНЯ' } else { "осталось $days дн." })
        } catch { "  $($f.Name): не разобрать" }
        if ($f.Name -notmatch '^[\w.-]+\.oke$') { "  ⚠ имя со скобками/пробелом — OCCT такой файл НЕ ВИДИТ" }
    }
}
