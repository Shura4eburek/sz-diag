namespace SzDiag.Desk.Services;

/// <summary>Лог окна в `logs\desk-&lt;дата&gt;.log` рядом с exe — по образцу HubLog: у GUI нет
/// консоли, и без файла упавший опрос или сессию нечем разбирать.</summary>
public static class DeskLog
{
    private static readonly object Gate = new();
    private static string? _dir;
    public const int RetentionDays = 14;

    public static void Init(string? baseDir = null)
    {
        _dir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.EnumerateFiles(_dir, "desk-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays)) File.Delete(f);
        }
        catch { _dir = null; }
    }

    public static void Write(string line)
    {
        if (_dir is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"desk-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { /* лог не должен ронять окно */ }
        }
    }
}
