$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Віддати файл з клієнта на хост через stdout `szcli exec`, коли `pull` і SMB не працюють (СЗ 161946).
#
# Грабля: у WinPE `szcli pull` висне до таймауту (бэклог п.215), а SMB не піднятий — і при цьому
# PowerShell мовчки пише «на шару» в локальний `X:\<ip>\...` (п.216). Робочим лишається лише
# stdout exec'а: він швидкий, але ріже вивід на 200k символів. Тому: пакуємо в zip і віддаємо
# шматками base64 по 120k.
#
# Як користуватись:
#   1) $Src — що пакувати (папка або файл), zip лягає в $Zip;
#   2) $Chunk — номер шматка (0,1,2,…); ганяти, доки не з'явиться ###EMPTY;
#   3) на хості склеїти вміст між ###BEGIN/###END, прибрати переноси, декодувати base64
#      і звірити sha256 з тим, що надрукував крок 0.
# 2.5 МБ дампів стискаються у ~190 КБ = 3 виклики.

$Src   = 'D:\szdiag-artifacts'
$Zip   = 'D:\szdiag-out.zip'
$Chunk = 0          # ← міняти між викликами
$Size  = 120000

if (-not (Test-Path $Zip) -or $Chunk -eq 0) {
    Remove-Item $Zip -Force -EA SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if ((Get-Item $Src).PSIsContainer) {
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $Src, $Zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    } else {
        $tmp = 'D:\szdiag-out-stage'
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        New-Item -ItemType Directory $tmp | Out-Null
        Copy-Item $Src $tmp -Force
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $tmp, $Zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    }
    $z = Get-Item $Zip
    "zip: {0:N0} KB  sha256={1}" -f ($z.Length/1KB), (Get-FileHash $Zip -Algorithm SHA256).Hash
    "шматків по $Size символів: {0}" -f [math]::Ceiling(([math]::Ceiling($z.Length/3)*4)/$Size)
}

$b = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Zip))
$start = $Chunk * $Size
if ($start -ge $b.Length) { "###EMPTY $($b.Length)"; exit 0 }
$take = [Math]::Min($Size, $b.Length - $start)
'###BEGIN'
$b.Substring($start, $take)
"###END $($b.Length)"
