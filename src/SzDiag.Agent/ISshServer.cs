namespace SzDiag.Agent;

/// <summary>Жизненный цикл портативного sshd. Абстракция ради подмены в тестах
/// (реальный Start ждёт готовности порта и ходит в систему).</summary>
public interface ISshServer
{
    /// <summary>Свежие host-ключи + конфиг + запуск sshd под SYSTEM; ждёт готовности порта.</summary>
    void Start(int port, string authorizedKeyLine, string taskName);

    /// <summary>Снять sshd-задачу и добить наш sshd (идемпотентно).</summary>
    void Stop(string taskName);

    /// <summary>Рабочая папка sshd (host-ключи, конфиг, authorized_keys).</summary>
    string WorkDir { get; }
}
