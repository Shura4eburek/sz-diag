using Spectre.Console;

namespace SzDiag.ConsoleUi;

/// <summary>Escape-последовательности VT и рендер Spectre-разметки в готовую ANSI-строку.</summary>
public static class Ansi
{
    private const string Esc = "\u001b";

    /// <summary>DECSTBM: ограничить прокрутку строками [top..bottom] (1-based, включительно).</summary>
    public static string SetScrollRegion(int top, int bottom) => $"{Esc}[{top};{bottom}r";

    /// <summary>Вернуть прокрутку на всё окно. Обязателен при выходе, иначе консоль
    /// остаётся с усечённой областью и после завершения процесса.</summary>
    public const string ResetScrollRegion = Esc + "[r";

    /// <summary>DECSC — сохранить позицию курсора (надёжнее SCO ESC[s в conhost).</summary>
    public const string SaveCursor = Esc + "7";

    /// <summary>DECRC — восстановить позицию курсора.</summary>
    public const string RestoreCursor = Esc + "8";

    /// <summary>CUP: поставить курсор (1-based).</summary>
    public static string MoveCursor(int row, int col) => $"{Esc}[{row};{col}H";

    /// <summary>EL: стереть от курсора до конца строки — чтобы хвост прошлой,
    /// более длинной, версии панели не оставался на экране.</summary>
    public const string ClearToEol = Esc + "[K";

    /// <summary>
    /// Разметка Spectre → ANSI-строка без переводов строки. Ширина профиля задана
    /// заведомо большой: перенос строк недопустим (строка панели должна остаться одной
    /// строкой), за длину отвечает поставщик строк, который получает доступную ширину.
    /// </summary>
    public static string MarkupToAnsi(string markup)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 10_000;
        console.Profile.Height = 10_000;
        console.Markup(markup);
        return writer.ToString().Replace("\r", "").Replace("\n", "");
    }
}
