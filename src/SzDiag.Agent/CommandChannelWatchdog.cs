namespace SzDiag.Agent;

/// <summary>Следит за тем, что командный канал агента жив, и решает, когда пора
/// перезапуститься.
///
/// Боль (СЗ 160705, бэклог п.58): после пробуждения машины из сна агент **перестал отвечать
/// на exec** — три попытки подряд в таймаут, включая скрипт из двух строк, — но снаружи
/// выглядел здоровым: heartbeat шёл, `szcli list` показывал `online` и активность «— готов».
/// Лечилось только грубо: `taskkill /F` + `schtasks /run /tn szdiag-autostart-<СЗ>`.
///
/// Логика вынесена отдельно от проводки, чтобы её можно было проверить без реального
/// зависания: класс считает подряд идущие неудачи пробы и говорит, когда лечиться.</summary>
public sealed class CommandChannelWatchdog
{
    private readonly int _failuresBeforeHeal;
    private int _consecutiveFailures;

    /// <param name="failuresBeforeHeal">Сколько неудачных проб подряд считать зависанием.
    /// Одна неудача — норма: под OCCT дочерний PowerShell стартует минутами (п.35).</param>
    public CommandChannelWatchdog(int failuresBeforeHeal = 3)
        => _failuresBeforeHeal = failuresBeforeHeal;

    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Учесть результат очередной пробы. true — канал завис, пора перезапускаться.</summary>
    public bool Observe(bool probeSucceeded)
    {
        if (probeSucceeded)
        {
            _consecutiveFailures = 0;
            return false;
        }
        _consecutiveFailures++;
        return _consecutiveFailures >= _failuresBeforeHeal;
    }

    /// <summary>Сбросить счётчик (например, после успешного самолечения).</summary>
    public void Reset() => _consecutiveFailures = 0;

    /// <summary>Проба: запускаем тривиальный PowerShell тем же путём, что и exec. Именно он
    /// на 160705 и залипал, поэтому проверять надо его, а не просто «процесс жив».</summary>
    public static bool Probe(IPowerShellRunner ps, TimeSpan timeout)
    {
        try
        {
            var r = ps.Run("'ok'", throwOnError: false, timeout: timeout);
            return r.ExitCode == 0 && r.StdOut.Contains("ok", StringComparison.Ordinal);
        }
        catch
        {
            return false;   // таймаут пробы — это и есть залипший канал
        }
    }

    /// <summary>Команда самолечения: запустить автостарт-задачу через паузу и выйти самим.
    /// Через задачу (а не напрямую) — потому что новый экземпляр не поднимется, пока живёт
    /// старый (мьютекс единственного агента), и потому что это ровно тот способ, которым
    /// зависшего агента чинили руками.</summary>
    public static string BuildSelfHealCommand(string autostartTaskName)
        => $"Start-Process cmd -ArgumentList '/c timeout /t 5 /nobreak >nul & schtasks /run /tn \"{autostartTaskName}\"' " +
           "-WindowStyle Hidden";
}
