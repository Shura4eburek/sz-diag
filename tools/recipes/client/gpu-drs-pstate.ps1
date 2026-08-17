$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Читает из базы профилей драйвера NVIDIA (nvdrsdb0.bin) настройку «Керування живленням»
# (NVAPI-параметр PREFERRED_PSTATE, id 0x1057EB71) — ту самую, что в панели называется
# «Оптимальне живлення / Адаптивний / Максимальна продуктивність».
#
# Грабля (СЗ 161190): карта висит в P0 (1792/7201) на 0 % загрузки, вентилятор 71 % — жалоба
# клиента «кулери на максимальних обертах» ровно про это. Приборно доказано, что железо
# исправно (до входа в сессию карта в P8, вентилятор 0 %), значит причина — применяемая
# настройка драйвера. Панель NVIDIA хранит её в бинарнике, из GUI по SSH её не посмотришь,
# а без цифры вердикт «это настройка, а не дефект карты» остаётся словами.
#
# Значения PREFERRED_PSTATE: 0x0 = адаптивный (заводское), 0x1 = максимальная
# производительность (карта не уходит из P0), 0x2 = оптимальное энергопотребление,
# 0x3 = минимальный уровень.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-drs-pstate.ps1

$drs = Join-Path $env:ProgramData 'NVIDIA Corporation\Drs'
$db = Join-Path $drs 'nvdrsdb0.bin'
if (-not (Test-Path $db)) { "нет $db"; return }
"файл: $db, {0:N0} б, изменён {1:dd.MM.yyyy HH:mm}" -f (Get-Item $db).Length, (Get-Item $db).LastWriteTime

$bytes = [IO.File]::ReadAllBytes($db)
# id хранится little-endian: 0x1057EB71 -> 71 EB 57 10
$sig = [byte[]](0x71, 0xEB, 0x57, 0x10)
$names = @{ 0 = 'адаптивный (заводское)'; 1 = 'МАКСИМАЛЬНАЯ ПРОИЗВОДИТЕЛЬНОСТЬ (карта не уходит из P0)';
    2 = 'оптимальное энергопотребление'; 3 = 'минимальный уровень' }

$hits = 0
for ($i = 0; $i -lt $bytes.Length - 16; $i++) {
    if ($bytes[$i] -ne $sig[0] -or $bytes[$i + 1] -ne $sig[1] -or $bytes[$i + 2] -ne $sig[2] -or $bytes[$i + 3] -ne $sig[3]) { continue }
    $hits++
    # За идентификатором идёт тип/значение; печатаем сырой хвост, чтобы не гадать о раскладке,
    # плюс наиболее вероятное поле значения — первый DWORD после заголовка параметра.
    $tail = ($bytes[($i + 4)..($i + 15)] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    $val = [BitConverter]::ToUInt32($bytes, $i + 8)
    $n = $names[[int]$val]
    if (-not $n) { $n = 'неизвестное значение' }
    "   [{0}] смещение 0x{1:X}: хвост {2}" -f $hits, $i, $tail
    "        значение = 0x{0:X} -> {1}" -f $val, $n
}
if (-not $hits) { '   параметр PREFERRED_PSTATE в базе не найден — настройка не менялась (заводской дефолт)' }

'== для сверки: что карта делает прямо сейчас'
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (Test-Path $smi) {
    '   ' + ((& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,clocks.current.memory,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '')
}
'== профили в базе (какие приложения вообще прописаны)'
$txt = [Text.Encoding]::Unicode.GetString($bytes)
[regex]::Matches($txt, '[A-Za-z0-9_\-\.]{4,40}\.exe') | ForEach-Object { $_.Value } |
    Sort-Object -Unique | Select-Object -First 40 | ForEach-Object { "   $_" }
