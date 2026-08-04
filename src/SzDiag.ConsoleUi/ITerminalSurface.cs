namespace SzDiag.ConsoleUi;

/// <summary>Терминал глазами панели: размеры и запись сырой ANSI-строки.
/// Существует ради тестируемости — реальная реализация одна.</summary>
public interface ITerminalSurface
{
    int Width { get; }
    int Height { get; }
    bool OutputRedirected { get; }
    /// <summary>Пишет как есть, без перевода строки.</summary>
    void Write(string raw);
}
