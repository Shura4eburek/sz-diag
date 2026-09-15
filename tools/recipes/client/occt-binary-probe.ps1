$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ЧЕМ В ЭТОЙ РАЗДАЧЕ ЗАПУСКАЕТСЯ OCCT: `OCCTCmd.exe` или `OCCTEnterprise.exe`.
#
# Грабля (СЗ 162003, 15.09.2026): все рецепты `start-occt-*.ps1` зашиты на `OCCTCmd.exe`,
# а в `client-tools\occt` лежит `OCCTEnterprise.exe` (раздачу меняли 04.09). Задача
# регистрируется и стартует, но падает с `LastTaskResult=0x80070002` («файл не найден»),
# при этом приёмка по памяти честно говорит «тест не взял память». Проверять имя бинаря
# надо ДО запуска трёхчасового прогона, а не после.
#
#   szcli exec <СЗ> -f tools\recipes\client\occt-binary-probe.ps1

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'агент не найден — не от чего считать путь к tools\occt'; return }
$roots = @(
    (Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'),
    (Join-Path $env:ProgramData 'szdiag\tools\occt'),
    'C:\OCCT'
) | Where-Object { Test-Path $_ }
if (-not $roots) { 'папки occt нет ни в одном известном месте — сначала szcli push <СЗ> occt'; return }

foreach ($r in $roots) {
    "== $r"
    Get-ChildItem $r -File -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending |
        ForEach-Object { '   {0,-34} {1,10:N1} МБ  {2:yyyy-MM-dd HH:mm}' -f $_.Name, ($_.Length / 1MB), $_.LastWriteTime }

    foreach ($exe in 'OCCTCmd.exe', 'OCCTEnterprise.exe', 'OCCT.exe') {
        $p = Join-Path $r $exe
        if (Test-Path $p) {
            $v = (Get-Item $p).VersionInfo
            '   => {0}: ЕСТЬ, версия {1}' -f $exe, $v.FileVersion
        }
    }
}

# Лицензия: имя строго license.oke, иначе OCCT её не видит (161716).
foreach ($r in $roots) {
    $oke = Get-ChildItem $r -Filter '*.oke' -ErrorAction SilentlyContinue
    foreach ($f in $oke) { 'лицензия: {0} ({1})' -f $f.FullName, $(if ($f.Name -eq 'license.oke') { 'имя верное' } else { 'ИМЯ НЕВЕРНОЕ — OCCT не увидит' }) }
}
