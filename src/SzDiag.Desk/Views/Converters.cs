using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Views;

public static class Converters
{
    private static IBrush Res(string key)
        => Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

    public static readonly IValueConverter WarnToBrush =
        new FuncValueConverter<bool, IBrush>(w => w ? Res("Warn") : Res("Text.Secondary"));

    /// <summary>Точка статуса СЗ. «Нет связи» — оранжевым, не красным: уверенно «вырубон» по
    /// одному молчанию heartbeat не говорим (бэклог п.42); красный — только сломанный откат.</summary>
    public static readonly IValueConverter LivenessToBrush = new FuncValueConverter<SzLivenessState, IBrush>(s => s switch
    {
        SzLivenessState.Online => Res("Ok"),
        SzLivenessState.LagSuspected => Res("Text.Tertiary"),
        SzLivenessState.NoContact => Res("Warn"),
        _ => Res("Bad"),
    });

    public static readonly IValueConverter LivenessToText = new FuncValueConverter<SzLivenessState, string>(s => s switch
    {
        SzLivenessState.Online => "онлайн",
        SzLivenessState.LagSuspected => "лаг heartbeat?",
        SzLivenessState.NoContact => "нет связи",
        _ => "⚠ откат не завершён",
    });

    public static readonly IValueConverter TransferStateToBrush = new FuncValueConverter<TransferState, IBrush>(s => s switch
    {
        TransferState.Done => Res("Ok"),
        TransferState.Failed => Res("Bad"),
        _ => Res("Accent"),
    });

    /// <summary>Точка сессии на карточке СЗ: работает — акцент, ждёт разрешения — оранжевый,
    /// упала — красный, остальное — третий план.</summary>
    public static readonly IValueConverter SessionStateToBrush = new FuncValueConverter<SessionState?, IBrush>(s => s switch
    {
        SessionState.Working => Res("Accent"),
        SessionState.WaitingPermission => Res("Warn"),
        SessionState.AnsweringPeer => Res("Violet"),
        SessionState.Crashed => Res("Bad"),
        _ => Res("Text.Tertiary"),
    });

    public static readonly IValueConverter SessionStateToText = new FuncValueConverter<SessionState?, string>(s => s switch
    {
        SessionState.Working => "Claude работает",
        SessionState.WaitingPermission => "Claude ждёт разрешения",
        SessionState.AnsweringPeer => "Claude отвечает соседней сессии",
        SessionState.Crashed => "сессия упала",
        SessionState.Idle => "сессия готова",
        _ => "сессия остановлена",
    });
}
