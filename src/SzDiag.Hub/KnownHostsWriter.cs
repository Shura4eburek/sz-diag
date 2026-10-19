using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Файл known_hosts на каждую СЗ. Нужен, чтобы уйти от StrictHostKeyChecking=no:
/// имя quick tunnel'а публично и не аутентифицировано, и без пиннинга подмену не отличить.
///
/// Якорь доверия — управляющий канал (SignalR, аутентифицирован, по TLS): публичный ключ
/// приезжает оттуда, а не с того конца, к которому мы подключаемся.</summary>
public static class KnownHostsWriter
{
    /// <summary>Путь к файлу known_hosts этой СЗ. Номер валидируется: он приходит от
    /// наименее доверенной машины, и без проверки произвольная строка
    /// (<c>..\..\Windows\System32</c>) уводит запись куда угодно — тот же класс дыры,
    /// что Critical-5.</summary>
    public static string PathFor(string root, string sz)
    {
        if (!SzNumber.IsValid(sz)) throw new ArgumentException(SzNumber.Explain(sz), nameof(sz));
        return Path.Combine(Resolve(root), sz);
    }

    /// <summary>Относительный путь — от папки exe, а не от рабочего каталога (конвенция
    /// проекта: на CWD не завязываемся).</summary>
    private static string Resolve(string root)
        => Path.IsPathRooted(root) ? root : Path.Combine(AppContext.BaseDirectory, root);

    /// <summary>Перезаписывает файл целиком: после ребута клиента имя туннеля другое, и
    /// копить старые строки — значит однажды упереться в несовпадение ключей.</summary>
    public static string Write(string root, string sz, string host, string publicKeyLine)
    {
        var path = PathFor(root, sz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{host} {publicKeyLine.Trim()}\n");
        return path;
    }
}
