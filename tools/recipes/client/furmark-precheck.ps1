# Чому `app run furmark` не стартує: чи є exe, чи є інтерактивна сесія, чи живий explorer, що за GPU.
# Народився на СЗ 169768: після перезавантаження `app run` → «процесс furmark не найден в интерактивной сессии через 10 с».
$p = 'C:\Users\User\Desktop\updater – home connection\tools\furmark\furmark.exe'
'exe exists: ' + (Test-Path -LiteralPath $p)
'== sessions'; quser 2>&1
'== explorer'; Get-Process explorer -IncludeUserName -EA 0 | Select-Object Id,SessionId,UserName,StartTime | Format-Table -AutoSize | Out-String
'== GPU'; Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,Status | Format-Table -AutoSize | Out-String
'== furmark log'
Get-ChildItem (Split-Path -LiteralPath $p) -Filter *.txt -EA 0 | Sort-Object LastWriteTime -Desc | Select-Object -First 2 | ForEach-Object { '-- ' + $_.Name; Get-Content -LiteralPath $_.FullName -Tail 15 }
