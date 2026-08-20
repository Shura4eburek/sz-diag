$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Тихая установка/обновление MSI Center (СЗ 161190) поверх текущей сборки.
#
# Грабля: MSI Center — Inno Setup (подпись MICRO-STAR валидна), значит мастер можно не кликать
# руками у машины: /VERYSILENT ставится под SYSTEM. GUI по SSH запускать нельзя — уходит в
# невидимую сессию и висит. Ставить ТОЛЬКО в detach: распаковка 555 МБ + установка модулей.
#
#   szcli exec <СЗ> -f tools\recipes\client\msi-center-install.ps1 --detach

$exe = 'C:\szdiag-tmp\MSI-Center\MSI Center_2.0.73.0.exe'
$log = 'C:\szdiag-tmp\msicenter-install.log'
if (-not (Test-Path $exe)) { "нет инсталлятора: $exe"; exit 1 }

'== ДО установки'
Get-ChildItem "${env:ProgramFiles(x86)}\MSI\MSI Center" -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'CentralServer|LEDKeeper' } |
    ForEach-Object { "   {0,-26} v{1,-16} {2:dd.MM.yyyy}" -f $_.Name, $_.VersionInfo.ProductVersion, $_.LastWriteTime }

"== запуск установки (VERYSILENT), $(Get-Date -Format 'HH:mm:ss')"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $exe -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCANCEL',"/LOG=`"$log`"" -PassThru -Wait
$sw.Stop()
"   exit code: {0}, время: {1:N0} с" -f $p.ExitCode, $sw.Elapsed.TotalSeconds

'== ПОСЛЕ установки'
Get-ChildItem "${env:ProgramFiles(x86)}\MSI\MSI Center" -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'CentralServer|LEDKeeper' } |
    ForEach-Object { "   {0,-26} v{1,-16} {2:dd.MM.yyyy}" -f $_.Name, $_.VersionInfo.ProductVersion, $_.LastWriteTime }

'== службы MSI'
Get-Service -Name 'MSI*','Mystic*' -ErrorAction SilentlyContinue |
    ForEach-Object { "   {0,-26} {1} / {2}" -f $_.Name, $_.Status, $_.StartType }

'== хвост лога инсталлятора'
if (Test-Path $log) { Get-Content $log -Tail 25 | ForEach-Object { "   $_" } } else { '   лога нет — инсталлятор мог не принять /VERYSILENT' }
