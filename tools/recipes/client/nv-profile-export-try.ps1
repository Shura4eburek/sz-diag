$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Подбор рабочего синтаксиса экспорта профилей nvidiaProfileInspector (СЗ 123456).
# Грабля: с '-export <путь>' инструмент завершается с кодом 0, но файла не создаёт — значит
# аргумент не тот. Проверяем варианты и смотрим, что реально появилось на диске.
#
#   szcli exec <СЗ> -f tools\recipes\client\nv-profile-export-try.ps1 --in-session

$ErrorActionPreference = 'SilentlyContinue'
$dir  = 'C:\Users\nekit\Desktop\client\tools\nvinspector'
$tool = Join-Path $dir 'nvidiaProfileInspector.exe'
$tmp  = 'C:\Windows\Temp'

$variants = @(
    @('-export', (Join-Path $tmp 'nvp1.xml')),
    @('-export', (Join-Path $tmp 'nvp2.nip')),
    @('-exportprofiles', (Join-Path $tmp 'nvp3.xml')),
    @('-e', (Join-Path $tmp 'nvp4.nip'))
)

foreach ($v in $variants) {
    $target = $v[1]
    Remove-Item $target -Force
    $p = Start-Process $tool -ArgumentList $v -PassThru -WindowStyle Hidden -WorkingDirectory $dir `
         -RedirectStandardOutput (Join-Path $tmp 'nvp-out.txt') -RedirectStandardError (Join-Path $tmp 'nvp-err.txt')
    $null = $p.WaitForExit(45000)
    $ok = Test-Path $target
    '   {0,-16} {1,-30} exit {2}  файл {3}' -f $v[0], (Split-Path $target -Leaf), $p.ExitCode, $(if ($ok) { '{0:N0} б' -f (Get-Item $target).Length } else { 'нет' })
    if ($ok) { break }
}

'=== что инструмент написал в консоль ==='
foreach ($f in 'nvp-out.txt','nvp-err.txt') {
    $c = Get-Content (Join-Path $tmp $f) | Select-Object -First 10
    if ($c) { "   -- $f"; $c | ForEach-Object { '      ' + $_ } }
}

'=== что вообще появилось в папке инструмента ==='
Get-ChildItem $dir | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) } |
    ForEach-Object { '   {0}  {1:N0} б  {2:HH:mm:ss}' -f $_.Name, $_.Length, $_.LastWriteTime }
