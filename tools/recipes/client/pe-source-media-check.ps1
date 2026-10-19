$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Проверка установочного носителя из PE (СЗ 162581: установка падает 0x8007025D
# "DecompressChunkedFile ... ill-formed data" на install.esd).
#
# Грабля: 0x8007025D на этапе применения образа хором списывают на "дохлый SSD", хотя
# ill-formed data — это мусор В ИСХОДНЫХ данных: либо файл на флешке действительно битый,
# либо его портит по дороге RAM/USB. Дискриминатор дешёвый: посчитать sha256 одного и того
# же файла ДВА раза подряд.
#   хеши равны   → файл читается стабильно; если он при этом битый, виноват образ/флешка
#   хеши разошлись → данные портятся при чтении в память — это RAM/USB-контроллер, не SSD
# Диск-приёмник в этом тесте вообще не участвует: читаем только источник.

$src = $null
foreach ($v in (Get-Volume | Where-Object { $_.DriveLetter })) {
    foreach ($name in 'install.esd', 'install.wim') {
        $p = ($v.DriveLetter + ':\sources\' + $name)
        if (Test-Path -LiteralPath $p) { $src = Get-Item -LiteralPath $p; break }
    }
    if ($src) { break }
}
if (-not $src) { Write-Output 'install.esd/wim не найден ни на одном томе'; exit 1 }

Write-Output ('Источник : ' + $src.FullName)
Write-Output ('Размер   : ' + [math]::Round($src.Length / 1GB, 2) + ' GB')
Write-Output ('Изменён  : ' + $src.LastWriteTime)
Write-Output ''

$h1 = (Get-FileHash -LiteralPath $src.FullName -Algorithm SHA256).Hash
Write-Output ('sha256 #1: ' + $h1)
$h2 = (Get-FileHash -LiteralPath $src.FullName -Algorithm SHA256).Hash
Write-Output ('sha256 #2: ' + $h2)
Write-Output ''
if ($h1 -eq $h2) {
    Write-Output 'ИТОГ: чтение стабильно (два прохода дали один хеш) — данные по дороге не бьются.'
} else {
    Write-Output 'ИТОГ: ХЕШИ РАЗОШЛИСЬ — источник читается по-разному. Это RAM/USB, а не SSD.'
}
