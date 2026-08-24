$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Где OCCT оставил следы прогона: отчёт/скриншоты/логи могут лечь не в SaveReportPath,
# а в профиль пользователя (Documents бывает "Документы" и/или перенаправлен в OneDrive).
$since = (Get-Date).AddHours(-3)
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$userHome = Join-Path 'C:\Users' $owner.User
"профиль: $userHome"

'== C:\OCCT целиком'
if (Test-Path 'C:\OCCT') {
    Get-ChildItem 'C:\OCCT' -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 25 |
        ForEach-Object { '   {0:yyyy-MM-dd HH:mm:ss} {1,9} б  {2}' -f $_.LastWriteTime, $_.Length, $_.FullName }
} else { '   нет папки' }

'== свежие файлы в профиле пользователя (3 ч), где в пути или имени есть occt'
Get-ChildItem $userHome -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt $since -and ($_.FullName -match 'occt' -or $_.Name -match 'occt') } |
    Select-Object -First 30 |
    ForEach-Object { '   {0:HH:mm:ss} {1,9} б  {2}' -f $_.LastWriteTime, $_.Length, $_.FullName }

'== свежее в AppData'
foreach ($d in @("$userHome\AppData\Roaming\OCCT", "$userHome\AppData\Local\OCCT", "$env:ProgramData\szdiag\tools\occt")) {
    if (Test-Path $d) {
        "   -- $d"
        Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 10 |
            ForEach-Object { '      {0:HH:mm:ss} {1,9} б  {2}' -f $_.LastWriteTime, $_.Length, $_.Name }
    }
}

'== журнал приложений: чем закончился OCCTCmd'
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$since} -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'OCCT' -or $_.ProviderName -match 'Application Error|Application Hang|.NET Runtime' } |
    Select-Object -First 15 |
    ForEach-Object { '   {0:HH:mm:ss} {1} {2}' -f $_.TimeCreated, $_.ProviderName, ($_.Message -split "`n")[0] }

'== события питания/WHEA за 3 часа'
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$since; Id=41,1001,6008,18,17,19,20,46,4101} -ErrorAction SilentlyContinue |
    Select-Object -First 20 |
    ForEach-Object { '   {0:HH:mm:ss} id={1} {2} {3}' -f $_.TimeCreated, $_.Id, $_.ProviderName, ($_.Message -split "`n")[0] }
