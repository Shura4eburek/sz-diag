using System.Diagnostics;

namespace SzDiag.Agent;

/// <summary>Сторож портативного sshd: процесс умер при живой сессии — поднимаем заново.
///
/// Боль (бэклог п.95, СЗ 161312): во время FurMark SSH перестал отвечать и **после теста не
/// поднялся** — `Test-NetConnection -Port 22` = False, а проверка через агента показала
/// `sshd=0`, то есть процесса просто нет. Канал был потерян на час; hub всё это время
/// показывал СЗ `online`, потому что heartbeat идёт (агент-то жив). Ровно тот случай, ради
/// которого sshd и запускается отдельной транзиентной задачей: перезапустить его дёшево.</summary>
public static class SshdWatchdog
{
    /// <summary>Жив ли хоть один sshd. Намеренно дешёвая проверка (`GetProcessesByName`, без
    /// PowerShell и без сетевых запросов): под 100 % нагрузкой sshd **отвечать** может
    /// перестать, и порт-проба дала бы ложную тревогу — а вот отсутствие процесса
    /// однозначно.</summary>
    public static bool IsAlive()
    {
        try
        {
            var procs = Process.GetProcessesByName("sshd");
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch
        {
            // Не смогли посмотреть — считаем живым: лишний перезапуск sshd рвёт активные
            // сессии, это хуже, чем пропущенная проверка.
            return true;
        }
    }

    /// <summary>Фоновый цикл. Проверяет раз в <paramref name="intervalSeconds"/>; заметив
    /// смерть, поднимает sshd тем же путём, что и `Open`.</summary>
    /// <param name="isAlive">Проверка живости (подменяется в тестах).</param>
    public static Task Start(ISshServer sshd, int port, string authorizedKeyLine, string taskName,
        CancellationToken ct, Action<string, string?> announce, int intervalSeconds = 60,
        Func<bool>? isAlive = null) =>
        Task.Run(async () =>
        {
            var alive = isAlive ?? IsAlive;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct); }
                catch (OperationCanceledException) { break; }

                if (alive()) continue;

                announce("sshd не найден в процессах — поднимаю заново (бэклог п.95).", null);
                try
                {
                    sshd.Start(port, authorizedKeyLine, taskName);
                    announce("sshd поднят заново — SSH-канал восстановлен.", null);
                }
                catch (Exception ex)
                {
                    // Не смогли — не страшно: exec/pull/push работают своим каналом,
                    // и следующая итерация попробует снова.
                    announce($"Поднять sshd не удалось: {ex.Message}", null);
                }
            }
        });
}
