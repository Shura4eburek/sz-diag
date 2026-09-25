using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels;

public sealed partial class SzItemViewModel : ObservableObject
{
    public SzItemViewModel(SessionInfo s, DateTimeOffset now) { Sz = s.Sz; Update(s, now); }

    public string Sz { get; }

    [ObservableProperty] private SessionInfo _info = null!;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private SzLivenessState _liveness;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReboots))] private int _rebootCount;

    public bool HasReboots => RebootCount > 0;

    public void Update(SessionInfo s, DateTimeOffset now)
    {
        Info = s;
        // Чем занята машина важнее имени хоста; строка железа (CPU · плата) появится вместе
        // с паспортом в инспекторе (часть 3 плана).
        Subtitle = string.IsNullOrEmpty(s.Activity) ? s.Hostname : s.Activity;
        Liveness = SzLiveness.Classify(s, now);
        RebootCount = s.RebootCount;
    }
}
