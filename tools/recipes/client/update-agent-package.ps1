$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ОБНОВИТЬ АГЕНТА НА КЛИЕНТЕ БЕЗ РУК У МАШИНЫ (то, что делает апдейтер, но headless).
#
# Грабля (СЗ 162003, 15.09.2026): `SzDiag.Updater.exe` после применения пакета запускает
# `SzDiag.Agent.exe` БЕЗ аргументов — агент спрашивает номер СЗ и в session 0 просто висит
# на вводе. Поэтому для удалённого обновления живой сессии апдейтер не годится: нужно
# заменить файлы и поднять агента автостарт-задачей (`--resume`), которая берёт СЗ из
# state.json.
#
# Второй мотив: обновлять приходилось ровно тогда, когда exec-канал уже мёртв (в этом и
# был фикс), то есть скрипт должен уметь работать через SSH одной командой.
#
#   szcli exec <СЗ> -f tools\recipes\client\update-agent-package.ps1 --param Sz=162003
#   (или по SSH: powershell -EncodedCommand <base64 этого файла с проставленным $Sz>)

$Sz = '162003'   # ← номер СЗ: нужен, чтобы поднять szdiag-autostart-<СЗ>

$baseDir = 'C:\Users\Administrator\Desktop\szdiag-updater-drop'
$cfg = Get-Content (Join-Path $baseDir 'appsettings.json') -Raw | ConvertFrom-Json
$hub = $cfg.HubUrl
$token = $cfg.AgentToken
"hub: $hub"

$headers = @{ 'X-SzDiag-Token' = $token }
$hostVersion = (Invoke-WebRequest -Uri "$hub/agent/version" -Headers $headers -UseBasicParsing -TimeoutSec 30).Content.Trim()
$localVersion = if (Test-Path (Join-Path $baseDir 'version.txt')) { (Get-Content (Join-Path $baseDir 'version.txt') -Raw).Trim() } else { '(нет)' }
"версия: клиент $localVersion -> хост $hostVersion"
if ($localVersion -eq $hostVersion) { 'версия актуальна — обновлять нечего'; return }

$zip = Join-Path $env:TEMP "szpkg-$(Get-Random).zip"
Invoke-WebRequest -Uri "$hub/agent/package" -Headers $headers -OutFile $zip -UseBasicParsing -TimeoutSec 600
$expected = (Invoke-WebRequest -Uri "$hub/agent/package.sha256" -Headers $headers -UseBasicParsing -TimeoutSec 30).Content.Trim()
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected.ToLowerInvariant()) { "sha256 НЕ СОШЁЛСЯ (ждали $expected, получили $actual) — обновление отменено"; Remove-Item $zip -Force; return }
"пакет скачан и сверен: $([math]::Round((Get-Item $zip).Length/1MB,1)) МБ"

# Агента снимаем ПОСЛЕ скачивания: если качать нечего, сессия не страдает.
Stop-Process -Name SzDiag.Agent -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# appsettings.json (локальный конфиг) и tools\ (тяжёлые тулы) не трогаем — как PackageApplier.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$arch = [IO.Compression.ZipFile]::OpenRead($zip)
$skipped = 0; $written = 0
foreach ($e in $arch.Entries) {
    if (-not $e.Name) { continue }
    if ($e.FullName -eq 'appsettings.json' -or $e.FullName -like 'tools/*' -or $e.FullName -like 'tools\*') { $skipped++; continue }
    $dst = Join-Path $baseDir ($e.FullName -replace '/', '\')
    New-Item -ItemType Directory -Path (Split-Path $dst -Parent) -Force | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
    $written++
}
$arch.Dispose()
Remove-Item $zip -Force
"распаковано файлов: $written, пропущено (конфиг/tools): $skipped"

schtasks /run /tn "szdiag-autostart-$Sz" | Out-Null
Start-Sleep -Seconds 12
$p = Get-Process SzDiag.Agent -ErrorAction SilentlyContinue
if ($p) { 'агент поднят: pid {0}, старт {1:HH:mm:ss}' -f $p.Id, $p.StartTime } else { 'АГЕНТ НЕ ПОДНЯЛСЯ — смотри logs\agent.log и задачу szdiag-autostart-' + $Sz }
