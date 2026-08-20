$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Через что именно MSI Center / LEDKeeper2 цепляет видеокарту (СЗ 161190).
#
# Грабля: доказано, что LEDKeeper2 держит карту в P0, но НЕ разобрано, чем он к ней лезет —
# подсветкой самой видяхи, NVAPI-опросом для синхронизации или модулями Graphics Fan Tool /
# AI Cooling / User Scenario, которые живут в том же процессе. От ответа зависит, можно ли
# погасить только GPU-часть, оставив подсветку корпуса живой (иначе клиент теряет функцию).
#
#   szcli exec <СЗ> -f tools\recipes\client\mystic-light-gpu-links.ps1

'== NVIDIA/GPU-библиотеки внутри процессов MSI'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'LEDKeeper|MSI|DCv2|Mystic' } | ForEach-Object {
    $p = $_
    $dlls = @()
    try { $dlls = $p.Modules | Where-Object { $_.ModuleName -match 'nvapi|nvml|nvcuda|nvopencl|d3d|dxgi|amd_ags|atiadl' } | Select-Object -Expand ModuleName -Unique } catch { $dlls = @('<нет доступа к модулям>') }
    if ($dlls) { "   {0,-22} pid {1,-7} → {2}" -f $p.Name, $p.Id, ($dlls -join ', ') }
}

'== Модули MSI Center: какие живут отдельными exe/службами'
foreach ($m in 'Graphics Fan Tool','AI Cooling','User Scenario','Mystic Light','System Diagnosis','Power Supply Unit') {
    $dir = Join-Path "${env:ProgramFiles(x86)}\MSI\MSI Center" $m
    if (-not (Test-Path $dir)) { "   {0,-20} нет каталога" -f $m; continue }
    $exes = Get-ChildItem $dir -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch 'unins' } | Select-Object -Expand Name
    $running = @()
    foreach ($e in $exes) { if (Get-Process ($e -replace '\.exe$','') -ErrorAction SilentlyContinue) { $running += $e } }
    $runTxt = '—'
    if ($running) { $runTxt = $running -join ',' }
    "   {0,-20} exe: {1,-40} запущено: {2}" -f $m, (($exes | Select-Object -First 3) -join ','), $runTxt
}

'== Конфиги Mystic Light: упоминания видеокарты (VGA/GPU/NVIDIA)'
$cfgRoots = @("$env:ProgramData\MSI", "${env:ProgramFiles(x86)}\MSI\MSI Center\Mystic Light", "$env:LOCALAPPDATA\Packages")
foreach ($r in $cfgRoots) {
    if (-not (Test-Path $r)) { continue }
    Get-ChildItem $r -Recurse -File -Include '*.json','*.xml','*.ini','*.dat','*.cfg' -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -lt 512KB } | ForEach-Object {
            $hit = Select-String -Path $_.FullName -Pattern 'VGA|GraphicsCard|GPU|NVIDIA|GeForce' -ErrorAction SilentlyContinue |
                   Select-Object -First 2
            if ($hit) {
                "   {0}" -f $_.FullName
                $hit | ForEach-Object { "      стр.{0}: {1}" -f $_.LineNumber, ($_.Line.Trim() -replace '\s+',' ' | ForEach-Object { if ($_.Length -gt 150) { $_.Substring(0,150) + '…' } else { $_ } }) }
            }
        }
}

'== Плагины/DLL устройств Mystic Light (кандидаты на точечное отключение)'
Get-ChildItem "${env:ProgramFiles(x86)}\MSI\MSI Center\Mystic Light" -Recurse -File -Include '*.dll' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'VGA|GPU|NV|Graphic|Nvidia' } |
    ForEach-Object { "   {0,-46} {1:N0} КБ  {2:dd.MM.yyyy}" -f $_.Name, ($_.Length/1KB), $_.LastWriteTime }
