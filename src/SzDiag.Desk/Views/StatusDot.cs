using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SzDiag.Desk.Views;

/// <summary>Статус по StatusDot Shirabe: у каждого своя форма, поэтому различимы и без цвета.</summary>
public enum DotKind
{
    /// <summary>Работает — заливка.</summary>
    Run,
    /// <summary>Ждёт ответа — кольцо.</summary>
    Ask,
    /// <summary>Ждёт подтверждения — ромб.</summary>
    Approve,
    /// <summary>Ошибка — треугольник.</summary>
    Err,
    /// <summary>Завершена / неактивна — тонкий контур.</summary>
    Done,
}

/// <summary>Знак статуса 10px. Пульсирует только <see cref="Live"/> (класс <c>live</c> в теме) —
/// единственная анимация интерфейса, кроме спиннера.</summary>
public sealed class StatusDot : Control
{
    public static readonly StyledProperty<DotKind> KindProperty =
        AvaloniaProperty.Register<StatusDot, DotKind>(nameof(Kind), DotKind.Done);

    public static readonly StyledProperty<bool> LiveProperty =
        AvaloniaProperty.Register<StatusDot, bool>(nameof(Live));

    static StatusDot()
    {
        AffectsRender<StatusDot>(KindProperty);
        WidthProperty.OverrideDefaultValue<StatusDot>(10);
        HeightProperty.OverrideDefaultValue<StatusDot>(10);
        VerticalAlignmentProperty.OverrideDefaultValue<StatusDot>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public DotKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool Live
    {
        get => GetValue(LiveProperty);
        set => SetValue(LiveProperty, value);
    }

    public StatusDot()
    {
        // Кисть берётся из ресурсов в Render: сменилась тема или словарь токенов — перерисовать.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == LiveProperty)
            Classes.Set("live", Live && Kind == DotKind.Run);
    }

    public static string BrushKey(DotKind k) => k switch
    {
        DotKind.Run => "Status.Running",
        DotKind.Ask => "Status.Waiting",
        DotKind.Approve => "Status.Approval",
        DotKind.Err => "Status.Error",
        _ => "Status.Done",
    };

    public override void Render(DrawingContext ctx)
    {
        var brush = this.TryFindResource(BrushKey(Kind), ActualThemeVariant, out var v) && v is IBrush b
            ? b : Brushes.Gray;
        var s = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = s / 2;
        switch (Kind)
        {
            case DotKind.Run:
                ctx.DrawEllipse(brush, null, c, r - 1, r - 1);
                break;
            case DotKind.Ask:
                ctx.DrawEllipse(null, new Pen(brush, 2), c, r - 1.5, r - 1.5);
                break;
            case DotKind.Approve:
                ctx.DrawGeometry(brush, null, Poly(
                    new(c.X, c.Y - r), new(c.X + r, c.Y), new(c.X, c.Y + r), new(c.X - r, c.Y)));
                break;
            case DotKind.Err:
                ctx.DrawGeometry(brush, null, Poly(
                    new(c.X, c.Y - r), new(c.X + r, c.Y + r * 0.8), new(c.X - r, c.Y + r * 0.8)));
                break;
            default:
                ctx.DrawEllipse(null, new Pen(brush, 1), c, r - 1.5, r - 1.5);
                break;
        }
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        gc.BeginFigure(pts[0], true);
        foreach (var p in pts.Skip(1)) gc.LineTo(p);
        gc.EndFigure(true);
        return g;
    }
}
