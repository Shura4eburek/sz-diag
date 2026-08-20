$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Скачать официальный инсталлятор MSI Center прямо на клиента (СЗ 161190).
#
# Грабля: пакет весит ~555 МБ, гнать его через `szcli push` (hub → агент) долго и без нужды —
# у клиента интернет есть, а download.msi.com отдаёт файл без Cloudflare-проверки (в отличие
# от www.msi.com, который на 403 режет и curl с хоста, и Invoke-WebRequest с клиента).
# Запускать ТОЛЬКО в detach: под SYSTEM качается несколько минут.
#
#   szcli exec <СЗ> -f tools\recipes\client\msi-center-download.ps1 --detach

$url = 'https://download.msi.com/uti_exe/desktop/MSI-Center.zip'
$dir = 'C:\szdiag-tmp'
$zip = Join-Path $dir 'MSI-Center.zip'

New-Item -ItemType Directory -Force -Path $dir | Out-Null
"цель: $zip"
$free = (Get-PSDrive C).Free / 1GB
"свободно на C: {0:N1} ГБ" -f $free
if ($free -lt 3) { "МАЛО МЕСТА — стоп"; exit 1 }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    (New-Object Net.WebClient).DownloadFile($url, $zip)
} catch {
    "ОШИБКА скачивания: " + $_.Exception.Message
    exit 1
}
$sw.Stop()
$f = Get-Item $zip
"скачано: {0:N1} МБ за {1:N0} с ({2:N1} МБ/с)" -f ($f.Length/1MB), $sw.Elapsed.TotalSeconds, (($f.Length/1MB)/$sw.Elapsed.TotalSeconds)
"sha256: " + (Get-FileHash $zip -Algorithm SHA256).Hash

$dst = Join-Path $dir 'MSI-Center'
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $dst -Force
"== содержимое пакета"
Get-ChildItem $dst -Recurse -File | Select-Object -First 40 |
    ForEach-Object { "   {0,10:N0} КБ  {1}" -f ($_.Length/1KB), $_.FullName.Substring($dst.Length + 1) }
