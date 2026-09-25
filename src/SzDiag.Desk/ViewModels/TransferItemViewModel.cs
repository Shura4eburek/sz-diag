using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;

namespace SzDiag.Desk.ViewModels;

public sealed partial class TransferItemViewModel : ObservableObject
{
    public TransferItemViewModel(TransferInfo t) { Id = t.Id; Update(t); }

    public string Id { get; }

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
            _ => (t.TotalBytes is > 0 ? $"{Mb(t.DoneBytes)} / {Mb(t.TotalBytes.Value)} МБ" : $"{Mb(t.DoneBytes)} МБ")
                 + $" · {Mb((long)t.BytesPerSecond)} МБ/с",
        };
    }

    private static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString();
}
