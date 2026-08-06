# Грабля: тело диагностической пробы — это PowerShell внутри C#-строки. Ошибка синтаксиса
# (незакрытая скобка, кривой if-как-выражение) вылезает только на живой машине, где секция
# приезжает пустой или с «ошибка: код 1» — и час уходит на выяснение, что виноват не клиент.
#
# Рецепт парсит ВСЕ пробы штатным парсером PowerShell до выезда на заявку.
# Запуск (после dotnet build):
#   pwsh -File tools\recipes\host\check-probe-syntax.ps1
param(
    [string]$Dll = "src/SzDiag.Agent/bin/Debug/net8.0/SzDiag.Agent.dll"
)

if (-not (Test-Path $Dll)) { throw "нет сборки агента: $Dll (сначала dotnet build)" }
Add-Type -Path $Dll

$bad = 0
foreach ($s in [SzDiag.Agent.DiagnosticProbes]::Suite.Steps) {
    $errors = $null; $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseInput($s.Run, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        $bad++
        Write-Host "СЕКЦИЯ $($s.Id):" -ForegroundColor Red
        $errors | ForEach-Object { "  строка $($_.Extent.StartLineNumber): $($_.Message)" }
    }
}

if ($bad -eq 0) { Write-Host "Все пробы разбираются без ошибок." -ForegroundColor Green }
else { Write-Host "Секций с ошибками: $bad" -ForegroundColor Red; exit 1 }
