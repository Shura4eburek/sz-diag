$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Точные времена сегодняшних TDR и что вокруг них происходило (СЗ 161190).
#
# Грабля: агрегаты livekernel-timeline.ps1 дают «6 событий за час», но не отвечают, ЧЕМ они
# вызваны. На 161190 события 0x117/0x1cc идут парами и подозрение падало на смены состояния
# сессии/дисплея (логин, засыпание монитора) — а это проверяется только сопоставлением
# по минутам с событиями входа, Display/4101 и питанием дисплея.
#
#   szcli exec <СЗ> -f tools\recipes\client\tdr-today.ps1

$since = (Get-Date).Date
'== LiveKernelEvent сегодня (точное время + код)'
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1001; StartTime=$since} -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'LiveKernelEvent' } |
    Sort-Object TimeCreated |
    ForEach-Object { '   {0:HH:mm:ss}  код {1}' -f $_.TimeCreated, $_.Properties[2].Value }

'== System за сегодня: драйвер дисплея, питание, вход в сессию'
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$since} -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match 'Display|nvlddmkm|Kernel-Power|Winlogon|Kernel-General' -or $_.Id -in 4101,1,12,13,42,107,6005,6008 } |
    Sort-Object TimeCreated |
    Select-Object -First 40 |
    ForEach-Object { '   {0:HH:mm:ss}  {1,-28} id {2}  {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, (($_.Message -split "`n")[0]) }

'== таймауты сна дисплея в текущей схеме питания'
powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE | Select-String 'Индекс текущ|Current AC|Current DC|индекс' | ForEach-Object { '   ' + $_.Line.Trim() }
