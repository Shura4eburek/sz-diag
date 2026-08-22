namespace SzDiag.Cli;

/// <summary>Сборка рабочей SSH-строки для `szcli target` (бэклог п.118): `ssh user@ip` без
/// `-i` не подключается, а host-ключ у каждой сессии свой — без выключенной проверки на
/// второй заявке с того же IP ssh ругается на смену ключа.</summary>
public static class TargetSsh
{
    /// <summary>Готовая к копированию строка. Без ключа — честный минимум (и предупреждение
    /// печатает вызывающий), с ключом — полная команда, работающая с первого раза.</summary>
    public static string BuildSshLine(string user, string ip, string? keyPath)
        => keyPath is null
            ? $"ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL {user}@{ip}"
            : $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL {user}@{ip}";

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
