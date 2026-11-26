$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Параметры BSOD без отладчика: события BugCheck 1001 (там все 4 параметра текстом) + заголовки
# минидампов на C: (dump-header.ps1 смотрит только W: — он для WinPE) + имя виновного модуля,
# если WER оставил Report.wer.
# Грабля (168175, 06.11): `diag run` даёт только код (0x116), а `szcli dump analyze`, на который
# ссылается README рецептов, в szcli нет; cdb на хосте (macOS) тоже нет. Для 0x116 главное —
# P3 (NTSTATUS, почему не восстановился) и P4 (причина TDR); их видно и без символов.
#
#   szcli exec <СЗ> -f tools\recipes\client\bugcheck-params.ps1 --timeout 120

$ErrorActionPreference = 'Continue'

'== BugCheck 1001 (System, WER-SystemErrorReporting), последние 20'
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'; Id=1001} -MaxEvents 20 -EA SilentlyContinue |
    ForEach-Object {
        $m = $_.Message -replace '\s+', ' '
        $bc = [regex]::Match($m, '0x[0-9a-fA-F]{8}\s*\([^)]*\)').Value
        ('[{0:yyyy-MM-dd HH:mm}] {1}' -f $_.TimeCreated, $(if ($bc) { $bc } else { $m.Substring(0, [Math]::Min(200, $m.Length)) }))
    }

''
'== Заголовки минидампов C:\Windows\Minidump (DUMP_HEADER64: код @0x38, P1..P4 @0x40..0x58)'
$files = @(Get-ChildItem 'C:\Windows\Minidump' -Filter *.dmp -EA SilentlyContinue | Sort-Object LastWriteTime)
foreach ($f in $files) {
    try {
        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
        $buf = New-Object byte[] 4096
        [void]$fs.Read($buf, 0, 4096)
        $fs.Close()
        $sig = [Text.Encoding]::ASCII.GetString($buf, 0, 8)
        $bc = [BitConverter]::ToUInt32($buf, 0x38)
        $p = foreach ($off in 0x40, 0x48, 0x50, 0x58) { '0x' + ([BitConverter]::ToUInt64($buf, $off)).ToString('X') }
        ('{0}  {1:yyyy-MM-dd HH:mm}  {2}  0x{3:X}  P1={4} P2={5} P3={6} P4={7}' -f $f.Name, $f.LastWriteTime, $sig, $bc, $p[0], $p[1], $p[2], $p[3])
        # Строки модулей видеостека в теле дампа — грубо, но показывает, чей драйвер в стеке
        $all = [IO.File]::ReadAllBytes($f.FullName)
        $u = [Text.Encoding]::Unicode.GetString($all)
        $mods = [regex]::Matches($u, '(?i)\b(nvlddmkm|dxgkrnl|dxgmms2|amdkmdag|atikmpag|watchdog|ntoskrnl)\.sys') | ForEach-Object { $_.Value.ToLower() } | Sort-Object -Unique
        if ($mods) { '    модули видеостека в дампе: ' + ($mods -join ', ') }
    } catch { ('{0}: не прочитан — {1}' -f $f.Name, $_.Exception.Message) }
}

''
'== WER ReportArchive / ReportQueue: BlueScreen и LiveKernelEvent (Sig/Module)'
$roots = 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive', 'C:\ProgramData\Microsoft\Windows\WER\ReportQueue'
$wer = foreach ($r in $roots) { Get-ChildItem $r -Recurse -Filter Report.wer -EA SilentlyContinue | Where-Object { $_.DirectoryName -match 'BlueScreen|Kernel_1[14]|LiveKernel' } }
$wer | Sort-Object LastWriteTime -Descending | Select-Object -First 12 | ForEach-Object {
    $t = Get-Content $_.FullName -Encoding Unicode -EA SilentlyContinue
    $sig = ($t | Where-Object { $_ -match '^Sig\[\d+\]\.Value=|^EventType=|^Response\.BucketId=|FriendlyEventName' }) -join ' | '
    ('[{0:yyyy-MM-dd HH:mm}] {1}' -f $_.LastWriteTime, (Split-Path $_.DirectoryName -Leaf))
    '    ' + $sig
}
