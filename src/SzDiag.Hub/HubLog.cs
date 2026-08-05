using System.Text;

namespace SzDiag.Hub;

/// <summary>Лог-файл hub рядом с exe.
///
/// Боль (04.08 и 05.08): hub дважды залипал насмерть — сначала после длинного `exec` с крупным
/// выводом, потом в thread pool starvation — и **разбирать было нечем**: весь вывод шёл только
/// в консоль запущенного окна, а окно к тому моменту было потеряно. Рестарт лечил симптом и
/// уносил причину с собой (бэклог п.41, п.50).
///
/// Файл на день (<c>hub-YYYYMMDD.log</c>), старые чистятся по сроку. Как и у агента, ошибки
/// логирования никогда не валят процесс: не смогли открыть — работаем без файла.</summary>
public static class HubLog
{
    /// <summary>Открывает лог текущего дня и подчищает старые. Никогда не бросает.</summary>
    /// <param name="dir">Каталог логов (относительный резолвится от папки exe).</param>
    /// <param name="retentionDays">Сколько дней хранить старые файлы (0 — не чистить).</param>
    public static TextWriter Init(string dir, int retentionDays = 14)
    {
        try
        {
            var full = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
            Directory.CreateDirectory(full);
            Prune(full, retentionDays);

            var path = Path.Combine(full, $"hub-{DateTime.Now:yyyyMMdd}.log");
            // FileShare.ReadWrite: лог должен читаться на живую (хвост в другом окне), а второй
            // экземпляр hub не должен падать из-за занятого файла.
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine();
            writer.WriteLine($"===== старт hub {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) =====");
            return writer;
        }
        catch
        {
            return TextWriter.Null;
        }
    }

    /// <summary>Удаляет логи старше указанного срока. Ошибки игнорирует — уборка не критична.</summary>
    public static void Prune(string dir, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(dir)) return;
        var edge = DateTime.Now.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(dir, "hub-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < edge) File.Delete(file);
            }
            catch { /* занят или нет прав — переживём */ }
        }
    }
}
