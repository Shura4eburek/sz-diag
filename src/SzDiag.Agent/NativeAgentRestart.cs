using System.Diagnostics;
using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Выполняет перезапуск агента напрямую, В ОБХОД `IPowerShellRunner`/exec-очереди
/// (бэклог п.202/п.215).
///
/// Боль (СЗ 161211/162367): `szcli agent restart` ходил как обычный exec-скрипт — тем же
/// каналом, что и всё остальное, — и был бесполезен ровно тогда, когда нужен: канал забит тем
/// же зависанием, которое агента и требовалось перезапустить. Дедлайн — отдельный SignalR-путь
/// (см. <c>HubRoutes.RestartAgent</c>), а сама регистрация задачи — свежий, независимый
/// `Process.Start`, а не поход через общий <see cref="IPowerShellRunner"/> (который синхронно
/// ждёт результат и мог быть занят/пуст под тем же зависанием, что и весь exec).
///
/// Текст самого PowerShell-скрипта (`AgentRestart.BuildScript`) переиспользуется как есть —
/// он уже использует `New-ScheduledTaskAction -Execute -Argument` (раздельные поля), а не
/// `schtasks /tr` с путём в кавычках, который на живых заявках ломался на пробелах в пути
/// (бэклог п.148).</summary>
public static class NativeAgentRestart
{
    /// <summary>Собирает ProcessStartInfo для independent powershell.exe — тестируемо отдельно
    /// от самого запуска.</summary>
    public static ProcessStartInfo BuildStartInfo(string sz, int delaySeconds = 45)
    {
        var script = AgentRestart.BuildScript(sz, delaySeconds);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        return psi;
    }

    /// <summary>Запускает регистрацию отложенной задачи-перезапуска независимым процессом —
    /// не дожидаясь и не деля канал с exec.</summary>
    public static void Run(string sz, int delaySeconds = 45) => Process.Start(BuildStartInfo(sz, delaySeconds));
}
