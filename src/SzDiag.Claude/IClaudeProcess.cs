namespace SzDiag.Claude;

/// <summary>Процесс `claude` одной сессии. Интерфейс — ради фейка в тестах сессии.</summary>
public interface IClaudeProcess
{
    /// <summary>Строка stdout (без перевода строки). Вызывается с фонового потока.</summary>
    event Action<string>? OutputLine;

    /// <summary>Процесс завершился (код, если известен) — после того как stdout дочитан.</summary>
    event Action<int?>? Exited;

    bool IsRunning { get; }

    /// <summary>Последние строки stderr — для карточки падения.</summary>
    IReadOnlyList<string> StderrTail { get; }

    void Start(ClaudeLaunch launch);

    Task WriteLineAsync(string line);

    /// <summary>Закрыть stdin, дать <paramref name="grace"/> на выход, затем убить всё дерево.</summary>
    Task StopAsync(TimeSpan grace);
}
