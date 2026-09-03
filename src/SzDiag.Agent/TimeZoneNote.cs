namespace SzDiag.Agent;

/// <summary>Явная метка часового пояса для секций, читающих время событий (Get-WinEvent /
/// TimeCreated). Обычная винда и WinPE могут жить в разных поясах: на СЗ 159948 в WinPE
/// стоял TZ -08:00, на хосте +03:00 — разница 11 часов молча превращала «выключилось днём»
/// в «выключилось ночью», и ложный таймлайн не ловился глазами (бэклог п.49). Отсутствие
/// метки — та же причина, по которой сплошной `Id=55` в выборке по MaxEvents читался как
/// «Kernel-Power 41 нет» на 160636 (п.31): молчание там, где должен быть явный факт.
///
/// Строго ASCII: тела проб уходят на клиента через EncodedCommand и читаются PowerShell 5.1.</summary>
public static class TimeZoneNote
{
    /// <summary>PowerShell-пролог: функция <c>Write-TzNote</c> печатает текущий часовой пояс,
    /// смещение от UTC и признак WinPE (тот же детектор — X: + startnet.cmd — что и
    /// <see cref="WinPeEnvironment"/> в C#, но независимо: тело пробы исполняется отдельным
    /// процессом powershell.exe и своего .NET-кода агента не видит).</summary>
    public static string PowerShellPrologue() => """
        function Write-TzNote {
            $tz = [System.TimeZoneInfo]::Local
            $offset = $tz.GetUtcOffset((Get-Date))
            $sign = if ($offset.Ticks -ge 0) { '+' } else { '-' }
            $isPe = (Test-Path "$env:SystemRoot\System32\startnet.cmd") -and ($env:SystemDrive -eq 'X:')
            "Vremya v etom otchete: poyas '{0}' (UTC{1}{2}), WinPE={3}. Sravnivaya s hostom ili drugim zapuskom - uchityvay raznitsu poyasov." -f `
                $tz.Id, $sign, $offset.ToString('hh\:mm'), $isPe
        }
        """;
}
