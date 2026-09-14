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

/// <summary>Чтение публичного host-ключа portable sshd для пиннинга на hub. Вынесено
/// отдельно: формат `*.pub` — «тип ключ комментарий», а в known_hosts комментарий не нужен
/// и только мешает сравнению.</summary>
public static class SshHostKeyReader
{
    /// <summary>Читает `ssh_host_ed25519_key.pub` из рабочей папки sshd и возвращает первые
    /// два поля («ssh-ed25519 AAAA…»). null — файла нет или он пуст: пиннинг тогда просто
    /// не включится, рабочий путь остаётся прежним.</summary>
    public static string? TryRead(string sshWorkDir)
    {
        try
        {
            var path = Path.Combine(sshWorkDir, "ssh_host_ed25519_key.pub");
            if (!File.Exists(path)) return null;

            var parts = File.ReadAllText(path).Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : null;
        }
        catch
        {
            return null;
        }
    }
}
