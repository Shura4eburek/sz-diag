namespace SzDiag.ConsoleUi;

/// <summary>Результат проверки: можно ли включать липкую панель и почему нет.</summary>
public readonly record struct StickyDecision(bool Enabled, string Reason);

/// <summary>Решение о включении липкого режима. Чистая функция — вся работа с реальной
/// консолью снаружи, чтобы решение можно было проверить таблицей случаев.</summary>
public static class StickyCapabilities
{
    /// <summary>Минимальная высота окна: ниже неё резерв под панель съедает лог целиком.</summary>
    public const int MinWindowHeight = 10;

    public static StickyDecision Evaluate(bool outputRedirected, bool vtEnabled,
        int windowHeight, bool configEnabled)
    {
        if (!configEnabled) return new(false, "выключено в конфиге (ConsoleUi:Sticky)");
        if (outputRedirected) return new(false, "вывод перенаправлен (не консоль)");
        if (!vtEnabled) return new(false, "терминал без поддержки VT");
        if (windowHeight < MinWindowHeight)
            return new(false, $"окно ниже {MinWindowHeight} строк");
        return new(true, "");
    }
}
