using System.Text;
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class StickyHeaderTests
{
    private const string Esc = "\u001b";

    private sealed class FakeSurface : ITerminalSurface
    {
        private readonly StringBuilder _sb = new();
        public int Width { get; set; } = 100;
        public int Height { get; set; } = 30;
        public bool OutputRedirected { get; set; }
        public void Write(string raw) { lock (_sb) _sb.Append(raw); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
        public void Clear() { lock (_sb) _sb.Clear(); }
    }

    private static StickyHeader? Start(FakeSurface surface, Func<int, IReadOnlyList<string>> render,
        bool configEnabled = true, bool vt = true, int lines = 2) =>
        StickyHeader.TryStart(render, new StickyOptions(Lines: lines, ConfigEnabled: configEnabled),
            surface, vtEnabled: vt, gate: new object(), autoRefresh: false);

    [Fact]
    public void TryStart_SetsScrollRegionBelowPanel()
    {
        var s = new FakeSurface { Height = 30 };
        using var h = Start(s, _ => new[] { "первая", "вторая" });

        Assert.NotNull(h);
        // 2 строки текста + разделитель = 3 зарезервированных, прокрутка с 4-й по 30-ю.
        Assert.Contains($"{Esc}[4;30r", s.Text);
    }

    [Fact]
    public void TryStart_ClearsAreaBelowPanel()
    {
        // Вывод, напечатанный до старта панели, остаётся на экране под ней; короткая
        // новая строка не затирает хвост длинной старой и текст наслаивается.
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "a", "b" });

        var text = s.Text;
        var move = text.IndexOf(Ansi.MoveCursor(4, 1), StringComparison.Ordinal);
        var clear = text.IndexOf(Ansi.ClearBelow, StringComparison.Ordinal);
        Assert.True(move >= 0, "курсор не переставлен в начало области прокрутки");
        Assert.True(clear > move, "область под панелью не очищена после установки курсора");
    }

    [Fact]
    public void TryStart_DoesNotPrintNewlines()
    {
        // Резерв делается сдвигом области прокрутки, а не печатью пустых строк:
        // печать скроллит уже написанное и сама создаёт наслоения.
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "a", "b" });
        Assert.DoesNotContain("\n", s.Text);
    }

    [Fact]
    public void TryStart_WhenRedirected_ReturnsNull()
    {
        var s = new FakeSurface { OutputRedirected = true };
        var h = Start(s, _ => new[] { "a", "b" });
        Assert.Null(h);
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void TryStart_WhenNoVt_ReturnsNull()
    {
        var s = new FakeSurface();
        Assert.Null(Start(s, _ => new[] { "a", "b" }, vt: false));
    }

    [Fact]
    public void TryStart_WhenConfigDisabled_ReturnsNull()
    {
        var s = new FakeSurface();
        Assert.Null(Start(s, _ => new[] { "a", "b" }, configEnabled: false));
    }

    [Fact]
    public void TryStart_WhenWindowTooShort_ReturnsNull()
    {
        var s = new FakeSurface { Height = 9 };
        Assert.Null(Start(s, _ => new[] { "a", "b" }));
    }

    [Fact]
    public void Refresh_DrawsTextAndRestoresCursor()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "СЗ 156864", "хоткеи" });
        s.Clear();

        h!.Refresh();

        var text = s.Text;
        Assert.StartsWith(Ansi.SaveCursor, text);
        Assert.EndsWith(Ansi.RestoreCursor, text);
        Assert.Contains("СЗ 156864", text);
        Assert.Contains("хоткеи", text);
        Assert.Contains(Ansi.ClearToEol, text);
    }

    [Fact]
    public void Refresh_PassesAvailableWidthToRenderer()
    {
        var s = new FakeSurface { Width = 77 };
        var seen = 0;
        using var h = Start(s, w => { seen = w; return new[] { "a", "b" }; });
        h!.Refresh();
        Assert.Equal(77, seen);
    }

    [Fact]
    public void Refresh_PadsMissingLines_SoRegionStaysStable()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "одна" });  // поставщик вернул меньше, чем Lines=2
        s.Clear();
        h!.Refresh();
        // Обе строки панели должны быть отрисованы (вторая — пустой с очисткой хвоста).
        Assert.Contains(Ansi.MoveCursor(1, 1), s.Text);
        Assert.Contains(Ansi.MoveCursor(2, 1), s.Text);
    }

    [Fact]
    public void Refresh_TrimsExtraLines_SoRegionStaysStable()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "a", "b", "c", "d" });
        s.Clear();
        h!.Refresh();
        Assert.DoesNotContain(Ansi.MoveCursor(4, 1), s.Text);
    }

    [Fact]
    public void Refresh_AfterResize_ReestablishesRegion()
    {
        var s = new FakeSurface { Height = 30 };
        using var h = Start(s, _ => new[] { "a", "b" });
        s.Clear();

        s.Height = 50;
        h!.Refresh();

        Assert.Contains($"{Esc}[4;50r", s.Text);
    }

    [Fact]
    public void Refresh_WhenWindowShrinksBelowThreshold_DisablesItself()
    {
        var s = new FakeSurface { Height = 30 };
        using var h = Start(s, _ => new[] { "a", "b" });
        s.Clear();

        s.Height = 5;
        h!.Refresh();
        Assert.Contains(Ansi.ResetScrollRegion, s.Text);

        s.Clear();
        h.Refresh();
        Assert.Equal("", s.Text);   // режим выключен насовсем, больше ничего не пишем
    }

    [Fact]
    public void Dispose_ResetsScrollRegion()
    {
        var s = new FakeSurface();
        var h = Start(s, _ => new[] { "a", "b" });
        s.Clear();
        h!.Dispose();
        Assert.Contains(Ansi.ResetScrollRegion, s.Text);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var s = new FakeSurface();
        var h = Start(s, _ => new[] { "a", "b" });
        h!.Dispose();
        s.Clear();
        h.Dispose();
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void Refresh_WhenRendererThrows_DoesNotPropagate()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => throw new InvalidOperationException("реестр моргнул"));
        var ex = Record.Exception(() => h!.Refresh());
        Assert.Null(ex);
    }
}
