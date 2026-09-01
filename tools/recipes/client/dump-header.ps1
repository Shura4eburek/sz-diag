$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Читаем DUMP_HEADER64 живого дампа: bugcheck-код лежит в первых 4 КБ,
# тащить ради него 4.7 ГБ на хост незачем.
# 162938: сюда же Minidump - с умирающего диска дамп целиком (2.5 МБ x5) через pull
# не выкачивается (чтение виснет на bad-блоках), а заголовок в 4 КБ отдаётся сразу.
$dirs = @('W:\Windows\LiveKernelReports', 'W:\Windows\Minidump')
$p = @()
foreach ($d in $dirs) { if (Test-Path $d) { $p += @(Get-ChildItem $d -Recurse -Filter *.dmp -EA SilentlyContinue) } }
$p = @($p | Sort-Object LastWriteTime)
foreach ($f in $p) {
    ('=== ' + $f.FullName + '  ' + [int]($f.Length/1MB) + ' MB  файл от ' + $f.LastWriteTime)
    $fs = [IO.File]::OpenRead($f.FullName)
    $buf = New-Object byte[] 4096
    [void]$fs.Read($buf, 0, 4096)
    $fs.Close()
    $sig = [Text.Encoding]::ASCII.GetString($buf, 0, 8)
    ('  signature   : ' + $sig)
    $bc = [BitConverter]::ToUInt32($buf, 0x38)
    ('  BugCheckCode: 0x' + $bc.ToString('X'))
    foreach ($off in 0x40,0x48,0x50,0x58) {
        $v = [BitConverter]::ToUInt64($buf, $off)
        ('  P' + [int](($off - 0x38)/8) + '          : 0x' + $v.ToString('X'))
    }
    ('  MachineImageType: 0x' + ([BitConverter]::ToUInt32($buf, 0x30)).ToString('X'))
    ('  NumberProcessors: ' + [BitConverter]::ToUInt32($buf, 0x34))
    # в хвосте заголовка обычно лежит строка с комментарием дампа
    $txt = [Text.Encoding]::ASCII.GetString($buf, 0, 4096)
    $m = [regex]::Matches($txt, '[\x20-\x7E]{8,}')
    $lines = @()
    foreach ($x in $m) { if ($x.Value -notmatch '^[A-Za-z0-9+/=]{40,}$') { $lines += $x.Value } }
    ('  строки: ' + (($lines | Select-Object -First 12) -join ' | '))
}
