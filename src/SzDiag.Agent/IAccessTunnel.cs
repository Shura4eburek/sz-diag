namespace SzDiag.Agent;

/// <summary>Жизненный цикл quick tunnel'а к нашему sshd. Абстракция ради подмены в тестах:
/// реальная реализация запускает внешний процесс и ходит в сеть, живой Cloudflare в
/// `dotnet test` не вызывается никогда.</summary>
public interface IAccessTunnel
{
    /// <summary>Поднять туннель на порт sshd. Возвращает выданное имя либо null, если имя не
    /// появилось за <paramref name="timeout"/>.
    ///
    /// null — НЕ отказ сессии: управляющий канал отдельный, и без SSH заявка продолжает
    /// работать через exec. Вызывающий обязан оставить доступ открытым.</summary>
    string? Start(int sshPort, string taskName, TimeSpan timeout);

    /// <summary>Снять туннель (идемпотентно): задача и процесс.</summary>
    void Stop(string taskName);
}
