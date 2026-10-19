using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;

namespace SzDiag.Desk.ViewModels;

public sealed partial class TransferItemViewModel : ObservableObject
{
    public TransferItemViewModel(TransferInfo t) { Id = t.Id; Sz = t.Sz; Update(t); }

    public string Id { get; }
    public string Sz { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private double? _percent;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private TransferState _state;

    public void Update(TransferInfo t)
    {
        State = t.State;
        Title = t.Direction == TransferDirection.Push ? $"push {t.What} → {t.Sz}" : $"pull {t.What} ← {t.Sz}";
        Percent = t.State == TransferState.Done ? 100
            : t.TotalBytes is > 0 ? Math.Min(100, t.DoneBytes * 100.0 / t.TotalBytes.Value)
            : null;
        Detail = t.State switch
        {
            TransferState.Done => $"готово · {t.Note}",
            TransferState.Failed => $"ошибка: {t.Note}",
            _ => (t.TotalBytes is > 0 ? Pair(t.DoneBytes, t.TotalBytes.Value) : Size(t.DoneBytes))
                 + $" · {Size((long)t.BytesPerSecond)}/с",
        };
    }

    private const long Mib = 1024 * 1024;

    /// <summary>«412 / 690 МБ», а при разных единицах — каждая со своей («512 КБ / 300 МБ»).</summary>
    private static string Pair(long done, long total)
        => done >= Mib && total >= Mib
            ? $"{Number(done)} / {Size(total)}"
            : $"{Size(done)} / {Size(total)}";

    /// <summary>Меньше мегабайта — в КБ, до 10 МБ — с десятыми: на плохой сети целые мегабайты
    /// показывали «0 МБ/с», и живая передача выглядела зависшей.</summary>
    private static string Size(long bytes)
        => bytes < Mib ? $"{bytes / 1024} КБ" : $"{Number(bytes)} МБ";

    private static string Number(long bytes)
    {
        var mb = bytes / (double)Mib;
        return mb < 10
            ? Math.Round(mb, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : ((long)mb).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
