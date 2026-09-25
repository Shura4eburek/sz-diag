using Avalonia;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Точки ломаной в прямоугольнике width×height: минимум внизу, максимум вверху. Пропуски
/// (датчик не отдал значение) выкидываются, X — по порядковому номеру отсчёта.</summary>
public static class Sparkline
{
    public static IReadOnlyList<Point> Points(IReadOnlyList<double?> values, double width, double height)
    {
        var present = values.Select((v, i) => (v, i)).Where(p => p.v is not null).ToList();
        if (present.Count < 2 || values.Count < 2) return Array.Empty<Point>();
        var min = present.Min(p => p.v!.Value);
        var max = present.Max(p => p.v!.Value);
        var span = max - min;
        return present.Select(p => new Point(
                p.i * width / (values.Count - 1),
                span <= 0 ? height / 2 : height - (p.v!.Value - min) / span * height))
            .ToList();
    }
}
