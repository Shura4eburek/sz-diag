$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Определить движок инсталлятора (Inno / NSIS / InstallShield / MSI / Advanced Installer),
# чтобы знать, есть ли у него тихий режим и какие ключи.
#
# Грабля (СЗ 161190): «просто запусти установщик» по SSH под SYSTEM = мастер уходит в невидимую
# сессию и висит без ответа. Прежде чем дёргать GUI, надо понять, умеет ли он /VERYSILENT.
#
#   szcli exec <СЗ> -f tools\recipes\client\installer-kind.ps1
# Путь задаётся ниже (правь под задачу).

$path = 'C:\szdiag-tmp\MSI-Center\MSI Center_2.0.73.0.exe'
if (-not (Test-Path $path)) { "нет файла: $path"; exit 1 }
$f = Get-Item $path
"файл:    {0}" -f $f.Name
"размер:  {0:N1} МБ" -f ($f.Length / 1MB)
"версия:  {0} / {1}" -f $f.VersionInfo.ProductVersion, $f.VersionInfo.FileVersion
"компания:{0}" -f $f.VersionInfo.CompanyName
"описание:{0}" -f $f.VersionInfo.FileDescription
"подпись: " + (Get-AuthenticodeSignature $path | ForEach-Object { "$($_.Status) — $($_.SignerCertificate.Subject)" })

# ищем сигнатуры движков в первых 4 МБ (там лежит стаб)
$buf = New-Object byte[] (4MB)
$fs = [IO.File]::OpenRead($path)
$read = $fs.Read($buf, 0, $buf.Length)
$fs.Close()
$txt = [Text.Encoding]::ASCII.GetString($buf, 0, $read)
$marks = @{
    'Inno Setup'       = 'Inno Setup|JR.Inno.Setup'
    'NSIS'             = 'Nullsoft.Install|NullsoftInst'
    'InstallShield'    = 'InstallShield'
    'Advanced Install' = 'Advanced Installer|caphyon'
    'WiX/MSI burn'     = 'WixBurn|\.wixburn'
    '7z SFX'           = '7-Zip|7zSfx'
}
'== найденные сигнатуры'
foreach ($k in $marks.Keys) { if ($txt -match $marks[$k]) { "   $k" } }
