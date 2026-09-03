namespace SzDiag.Cli;

/// <summary>Ждёт появления свежего <c>diag.md</c> в <c>reports\</c> после старта прогона —
/// вместо шаблонного пути с плейсхолдером таймстампа (бэклог п.60: точный таймстамп генерится
/// на агенте, поэтому CLI до этого места мог показать только образец пути, а по завершении
/// прогона вообще ничего не сообщал). Опрос локальной файловой системы, а не новый round-trip
/// по SignalR: hub и CLI живут на одном боксе, и файл появляется прямо в kb на диске.</summary>
public static class DiagCompletionWatcher
{
    /// <summary>Свежий diag.md в <paramref name="reportsDir"/> — самый новый из тех, что
    /// записаны не раньше <paramref name="after"/> (иначе можно словить отчёт от прошлого
    /// прогона, если новый ещё не долетел).</summary>
    public static (string Path, long Bytes)? FindLatest(string reportsDir, DateTime after)
    {
        if (!Directory.Exists(reportsDir)) return null;

        FileInfo? best = null;
        foreach (var dir in Directory.EnumerateDirectories(reportsDir))
        {
            var candidate = Path.Combine(dir, "diag.md");
            if (!File.Exists(candidate)) continue;
            var fi = new FileInfo(candidate);
            if (fi.LastWriteTime < after) continue;
            if (best is null || fi.LastWriteTime > best.LastWriteTime) best = fi;
        }
        return best is null ? null : (best.FullName, best.Length);
    }

    /// <summary>Опрашивает диск до появления файла или истечения таймаута. Не бросает —
    /// таймаут означает «прогон идёт дольше обычного», а не ошибку (секции events/reliability
    /// могут занять десятки секунд).</summary>
    public static async Task<(string Path, long Bytes)?> WaitAsync(
        string reportsDir, DateTime after, TimeSpan timeout, TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var found = FindLatest(reportsDir, after);
            if (found is not null) return found;
            if (DateTime.UtcNow >= deadline) return null;
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return null; }
        }
    }
}
