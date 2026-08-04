namespace SzDiag.ConsoleUi;

/// <summary>Настройки липкой панели.</summary>
/// <param name="Lines">Сколько строк текста в панели. Фиксировано, чтобы резерв под
/// панель не «дышал»: поставщик, вернувший больше или меньше, обрезается/добивается.</param>
/// <param name="ConfigEnabled">Рубильник из конфига.</param>
/// <param name="RefreshInterval">Период автоперерисовки.</param>
public sealed record StickyOptions(
    int Lines = 2,
    bool ConfigEnabled = true,
    TimeSpan? RefreshInterval = null);

/// <summary>
/// Липкая панель в верхних строках консоли. Работает через ANSI scroll region (DECSTBM):
/// область прокрутки сдвигается ниже панели, поэтому обычный вывод (логи ASP.NET,
/// Announce агента, вывод дочерних процессов) скроллит только нижнюю часть окна,
/// а панель остаётся на месте. Перехватывать логи не требуется.
/// </summary>
public sealed class StickyHeader : IDisposable
{
    private readonly Func<int, IReadOnlyList<string>> _render;
    private readonly ITerminalSurface _surface;
    private readonly object _gate;
    private readonly int _lines;
    private readonly int _reserved;      // строки текста + разделитель
    private readonly Timer? _timer;

    private int _knownHeight;
    private int _knownWidth;
    private bool _active;
    private bool _disposed;

    private StickyHeader(Func<int, IReadOnlyList<string>> render, ITerminalSurface surface,
        object gate, StickyOptions options, bool autoRefresh)
    {
        _render = render;
        _surface = surface;
        _gate = gate;
        _lines = options.Lines;
        _reserved = options.Lines + 1;   // +1 — разделительная линия под панелью
        _knownHeight = surface.Height;
        _knownWidth = surface.Width;
        _active = true;

        SetupRegion();
        Refresh();

        if (autoRefresh)
        {
            var period = options.RefreshInterval ?? TimeSpan.FromSeconds(1);
            _timer = new Timer(_ => Refresh(), null, period, period);
        }
    }

    /// <summary>
    /// Пытается включить липкий режим. Возвращает null, если условия не выполнены
    /// (перенаправленный вывод, нет VT, низкое окно, выключено конфигом) — вызывающий
    /// в этом случае просто работает как раньше, линейным выводом.
    /// </summary>
    /// <param name="render">Получает доступную ширину, возвращает строки со Spectre-разметкой.</param>
    /// <param name="gate">Тот же лок, что у <see cref="SyncedConsoleWriter"/>.</param>
    public static StickyHeader? TryStart(
        Func<int, IReadOnlyList<string>> render,
        StickyOptions options,
        ITerminalSurface surface,
        bool vtEnabled,
        object gate,
        bool autoRefresh = true)
    {
        var decision = StickyCapabilities.Evaluate(
            surface.OutputRedirected, vtEnabled, surface.Height, options.ConfigEnabled);
        if (!decision.Enabled) return null;

        return new StickyHeader(render, surface, gate, options, autoRefresh);
    }

    /// <summary>Резервирует место под панель и сдвигает область прокрутки вниз.</summary>
    private void SetupRegion()
    {
        lock (_gate)
        {
            _surface.Write(Ansi.SetScrollRegion(_reserved + 1, _knownHeight));
            // Курсор — в начало области прокрутки, иначе первый лог уйдёт под панель.
            _surface.Write(Ansi.MoveCursor(_reserved + 1, 1));
            // Всё, что напечатано до старта панели, осталось на экране под ней. Не стереть
            // его нельзя: короткая новая строка не затирает хвост длинной старой, и вывод
            // наслаивается. Сам текст никуда не девается — он продублирован в лог-файл.
            _surface.Write(Ansi.ClearBelow);
        }
    }

    /// <summary>Перерисовывает панель. Безопасно звать из любого потока.</summary>
    public void Refresh()
    {
        if (_disposed || !_active) return;

        // Ресайз: событий на Windows нет, поэтому сверяем размеры на каждом тике.
        var height = _surface.Height;
        var width = _surface.Width;
        if (height != _knownHeight || width != _knownWidth)
        {
            if (height < StickyCapabilities.MinWindowHeight)
            {
                // Окно ужали до неприличия — выключаемся насовсем, дальше линейный вывод.
                lock (_gate)
                {
                    _surface.Write(Ansi.ResetScrollRegion);
                    _active = false;
                }
                return;
            }
            _knownHeight = height;
            _knownWidth = width;
            lock (_gate) _surface.Write(Ansi.SetScrollRegion(_reserved + 1, _knownHeight));
        }

        IReadOnlyList<string> lines;
        try { lines = _render(_knownWidth); }
        catch { return; }   // упавший поставщик статуса не должен ронять процесс

        lock (_gate)
        {
            if (!_active) return;
            _surface.Write(Ansi.SaveCursor);
            for (var i = 0; i < _lines; i++)
            {
                var markup = i < lines.Count ? lines[i] : "";
                _surface.Write(Ansi.MoveCursor(i + 1, 1));
                _surface.Write(Ansi.ClearToEol);
                _surface.Write(Ansi.MarkupToAnsi(markup));
            }
            _surface.Write(Ansi.MoveCursor(_lines + 1, 1));
            _surface.Write(Ansi.ClearToEol);
            _surface.Write(Ansi.MarkupToAnsi($"[grey]{new string('─', Math.Max(0, _knownWidth - 1))}[/]"));
            _surface.Write(Ansi.RestoreCursor);
        }
    }

    /// <summary>Сбрасывает область прокрутки. Без этого консоль остаётся усечённой
    /// и после завершения процесса.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            _surface.Write(Ansi.ResetScrollRegion);
        }
    }
}
