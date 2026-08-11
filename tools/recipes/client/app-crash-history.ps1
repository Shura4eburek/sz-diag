$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# История падений приложений и LiveKernelEvent — по ВСЕЙ глубине журнала Application.
#
# Грабля (СЗ 160697): клиент жалуется на вылеты в конкретной игре, а штатная секция diag
# показывает только последние записи Application 1000 — крэша самой игры там не видно,
# зато видно 284 события LiveKernelEvent (0x1B0 / 0x193), по которым ничего не сказать:
# штатный вывод даёт только счётчик, без дат и без того, подтверждено ли событие дампом.
# Здесь: (1) все Application 1000/1026 сгруппированы по имени процесса с датами первого
# и последнего падения, (2) LiveKernelEvent с разбивкой по кодам и датам, (3) проверка,
# лежит ли рядом файл дампа — «событие без дампа» диагностической ценности почти не несёт.

$AppFilter = 'enlisted'   # подстрока имени процесса для детального разбора

'=== Application 1000 (Application Error) — vse za istoriyu zhurnala ==='
$crashes = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000 } -ErrorAction SilentlyContinue
if ($crashes) {
    $crashes | Group-Object { $_.Properties[0].Value } | Sort-Object Count -Descending | ForEach-Object {
        $first = ($_.Group | Sort-Object TimeCreated | Select-Object -First 1).TimeCreated
        $last = ($_.Group | Sort-Object TimeCreated | Select-Object -Last 1).TimeCreated
        ('   {0,-32} x{1,-4} {2:yyyy-MM-dd HH:mm} .. {3:yyyy-MM-dd HH:mm}' -f $_.Name, $_.Count, $first, $last)
    }
} else { '   (net sobytiy 1000)' }
''

'=== Application 1026 (.NET Runtime) ==='
$net = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1026 } -ErrorAction SilentlyContinue
if ($net) {
    $net | Group-Object { ($_.Message -split "`n")[0] } | Sort-Object Count -Descending |
        Select-Object -First 10 | ForEach-Object { ('   x{0,-4} {1}' -f $_.Count, $_.Name) }
} else { '   (net sobytiy 1026)' }
''

("=== Detalno po '" + $AppFilter + "' (1000/1001, polnye tela) ===")
$hits = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1001 } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match $AppFilter }
if ($hits) {
    $hits | Sort-Object TimeCreated | Select-Object -Last 20 | ForEach-Object {
        ('--- {0:yyyy-MM-dd HH:mm:ss}  Id={1}' -f $_.TimeCreated, $_.Id)
        ($_.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 12) -join "`n"
        ''
    }
} else { ("   (sovpadeniy po '" + $AppFilter + "' net — igra na etoy OS ne padala ili zhurnal zatert)") }
''

'=== LiveKernelEvent: kody, daty, nalichie dampa ==='
$live = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1001 } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'LiveKernelEvent' }
if ($live) {
    ('   vsego: ' + $live.Count)
    $live | Group-Object { if ($_.Message -match 'LiveKernelEvent\s*(?:Code:)?\s*([0-9a-fA-Fx]+)') { $matches[1] } else { '?' } } |
        Sort-Object Count -Descending | ForEach-Object {
            $first = ($_.Group | Sort-Object TimeCreated | Select-Object -First 1).TimeCreated
            $last = ($_.Group | Sort-Object TimeCreated | Select-Object -Last 1).TimeCreated
            ('   code {0,-8} x{1,-5} {2:yyyy-MM-dd HH:mm} .. {3:yyyy-MM-dd HH:mm}' -f $_.Name, $_.Count, $first, $last)
        }
    ''
    '   -- raspredelenie po dnyam (posledniye 14 dney s sobytiyami) --'
    $live | Group-Object { $_.TimeCreated.ToString('yyyy-MM-dd') } | Sort-Object Name -Descending |
        Select-Object -First 14 | ForEach-Object { ('   {0}  x{1}' -f $_.Name, $_.Count) }
    ''
    '   -- primer tela posledney zapisi --'
    ($live | Sort-Object TimeCreated | Select-Object -Last 1).Message -split "`r?`n" |
        Where-Object { $_.Trim() } | Select-Object -First 25
} else { '   (net LiveKernelEvent)' }
''

'=== Fayly otchetov WER (LiveKernelReports / WER queue) ==='
foreach ($p in @('C:\Windows\LiveKernelReports', "$env:ProgramData\Microsoft\Windows\WER\ReportQueue")) {
    ('--- ' + $p)
    if (Test-Path $p) {
        Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 25 | ForEach-Object {
                ('   {0:yyyy-MM-dd HH:mm}  {1,10:N0} KB  {2}' -f $_.LastWriteTime, ($_.Length / 1KB), $_.FullName)
            }
    } else { '   (net papki)' }
}
