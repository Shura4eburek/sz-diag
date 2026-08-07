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

# Заодно — живая проверка разбора бинарной записи WHEA (CPER, бэклог п.68) на синтетическом
# буфере: срез PowerShell-массива возвращает Object[], и конструктор Guid его молча не берёт —
# ошибка вылезала бы только на машине с фатальной ошибкой WHEA.
Invoke-Expression ([SzDiag.Agent.CperDecoder]::PowerShellPrologue())
$b = New-Object byte[] 400
[Text.Encoding]::ASCII.GetBytes('CPER').CopyTo($b, 0)
[BitConverter]::GetBytes([uint16]1).CopyTo($b, 10)     # SectionCount
[BitConverter]::GetBytes([uint32]1).CopyTo($b, 12)     # Severity = Fatal
([guid]'e8f56ffe-919c-4cc5-ba88-65abe14913bb').ToByteArray().CopyTo($b, 80)   # канал = MCE
[BitConverter]::GetBytes([uint32]200).CopyTo($b, 128)  # SectionOffset
[BitConverter]::GetBytes([uint32]64).CopyTo($b, 132)   # SectionLength
([guid]'dc3ea0b0-a144-4797-b95b-53fa242b6e1d').ToByteArray().CopyTo($b, 144)  # Processor Specific
[BitConverter]::GetBytes([uint32]1).CopyTo($b, 176)    # SectionSeverity
[BitConverter]::GetBytes([uint64]7).CopyTo($b, 208)    # LocalApicId = 7

$r = Parse-Cper $b
if ($r.Severity -ne 'Fatal' -or $r.ApicId -ne 7 -or $r.Notification -notlike 'MCE*') {
    Write-Host "Parse-Cper разобрал запись неверно: $($r | ConvertTo-Json -Compress)" -ForegroundColor Red
    exit 1
}
Write-Host "Parse-Cper: Fatal / MCE / APIC 7 — разбор корректен." -ForegroundColor Green
