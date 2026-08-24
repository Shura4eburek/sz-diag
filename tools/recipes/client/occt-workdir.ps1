$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что лежит в рабочей папке OCCT (Temp\OCCT) — там отчёт, лог и последние значения сенсоров.
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$dir = "C:\Users\$($owner.User)\AppData\Local\Temp\OCCT"
"== $dir (корень)"
Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending |
    ForEach-Object { '   {0:yyyy-MM-dd HH:mm:ss} {1,10} б  {2}' -f $_.LastWriteTime, $_.Length, $_.Name }
foreach ($n in 'occt.log','log.txt','OCCT.log','errors.txt') {
    $p = Join-Path $dir $n
    if (Test-Path $p) { "   --- $n"; Get-Content $p -Tail 60 | ForEach-Object { '      ' + $_ } }
}
'== любые .log/.txt свежее 3 ч в дереве Temp\OCCT'
Get-ChildItem $dir -Recurse -File -Include *.log,*.txt -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddHours(-3) } |
    ForEach-Object { '   {0:HH:mm:ss} {1,8} б  {2}' -f $_.LastWriteTime, $_.Length, $_.FullName }
'== история задачи szdiag-occtcomb-161716 в журнале планировщика'
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-TaskScheduler/Operational'; StartTime=(Get-Date).AddHours(-3)} -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'occtcomb' } | Select-Object -First 20 |
    ForEach-Object { '   {0:HH:mm:ss} id={1} {2}' -f $_.TimeCreated, $_.Id, (($_.Message -split "`n")[0]).Trim() }
