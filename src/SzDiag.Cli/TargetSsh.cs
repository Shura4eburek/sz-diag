namespace SzDiag.Cli;

/// <summary>Сборка рабочей SSH-строки для `szcli target` (бэклог п.118): `ssh user@ip` без
/// `-i` не подключается, а host-ключ у каждой сессии свой — без выключенной проверки на
/// второй заявке с того же IP ssh ругается на смену ключа.</summary>
public static class TargetSsh
{
    /// <summary>Команда-прокси для туннельного режима. `%h` подставляет сам ssh — имя
    /// туннеля не приходится писать в строку дважды.</summary>
    private const string ProxyCommand = "ProxyCommand cloudflared access ssh --hostname %h";

    /// <summary>Готовая к копированию строка. Без ключа — честный минимум (и предупреждение
    /// печатает вызывающий), с ключом — полная команда, работающая с первого раза.</summary>
    /// <param name="host">Адрес в прямом режиме, имя quick tunnel'а — в туннельном.</param>
    /// <param name="viaTunnel">У машины нет входящего порта, доступного хосту: идём через
    /// `cloudflared access ssh`.</param>
    /// <param name="knownHostsPath">Файл known_hosts этой СЗ. Задан — ходим со строгой
    /// проверкой host-ключа: имя quick tunnel'а публично и не аутентифицировано, без пиннинга
    /// подмену не отличить. Не задан (агент старой сборки) — прежнее поведение.</param>
    public static string BuildSshLine(string user, string host, string? keyPath,
        bool viaTunnel = false, string? knownHostsPath = null)
    {
        var parts = new List<string> { "ssh" };

        if (keyPath is not null) parts.Add($"-i \"{keyPath}\"");

        if (!string.IsNullOrWhiteSpace(knownHostsPath))
        {
            parts.Add("-o StrictHostKeyChecking=yes");
            parts.Add($"-o UserKnownHostsFile=\"{knownHostsPath}\"");
        }
        else
        {
            // Исторически: IP переиспользуются между заявками, и на второй заявке с того же
            // адреса ssh ругался на смену host-ключа (бэклог п.118).
            parts.Add("-o StrictHostKeyChecking=no");
            parts.Add("-o UserKnownHostsFile=NUL");
        }

        if (viaTunnel) parts.Add($"-o \"{ProxyCommand}\"");

        parts.Add($"{user}@{host}");
        return string.Join(' ', parts);
    }

    /// <summary>Ищет приватный ключ: сначала настроенный путь (`SshKeyPath` в конфиге CLI —
    /// его пишет build-dist), затем `secrets\svc_diag_key` вверх по дереву от папки CLI
    /// (репозиторий: CLI лежит в подпапке, ключ — в корне). null — не нашли.</summary>
    public static string? FindKey(string configured, string baseDir)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; dir is not null && depth < 6; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "secrets", "svc_diag_key");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
