$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Полный срез WHEA-Logger всех уровней. Породила СЗ 161498: штатная секция `diag run <СЗ> whea`
# упала с "The filename or extension is too long" (агент передаёт скрипт секции инлайном в
# powershell.exe и упирается в лимит длины командной строки) — а факт "WHEA пусто" критичен
# для разбора hard-off: он отличает сверку данных/питание от MCE.
#   szcli exec <СЗ> -f tools\recipes\client\whea-all.ps1

$ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger' } -ErrorAction SilentlyContinue
if (-not $ev) { 'WHEA: ПУСТО (ни одного события за всю историю журнала)'; return }

"WHEA: всего $($ev.Count) событий, первое $($ev[-1].TimeCreated), последнее $($ev[0].TimeCreated)"
'--- по Id/уровню ---'
$ev | Group-Object Id, LevelDisplayName | ForEach-Object { "  Id={0}  {1}" -f $_.Name, $_.Count }
'--- по дням ---'
$ev | Group-Object { $_.TimeCreated.ToString('yyyy-MM-dd') } | Sort-Object Name | ForEach-Object { "  {0}: {1}" -f $_.Name, $_.Count }
'--- последние 15 (детально) ---'
$ev | Select-Object -First 15 | ForEach-Object {
    "[{0:dd.MM.yyyy HH:mm:ss}] Id={1} {2}" -f $_.TimeCreated, $_.Id, $_.LevelDisplayName
    ($_.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) | ForEach-Object { "    $_" }
}
