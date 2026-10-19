using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.ViewModels;
using SzDiag.HubClient;

namespace SzDiag.Desk.Views;

public static class Converters
{
    private static IBrush Res(string key)
        => Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

    public static readonly IValueConverter WarnToBrush =
        new FuncValueConverter<bool, IBrush>(w => w ? Res("Status.Warning") : Res("Text.Secondary"));

    /// <summary>Статус СЗ. «Нет связи» — ромбом «ждёт», не ошибкой: уверенно «вырубон» по
    /// одному молчанию heartbeat не говорим (бэклог п.42); ошибка — только сломанный откат.
    /// Знаки живости — только у машины; состояние сессии на карточке рисуется иконкой чата, поэтому
    /// ромб «нет связи» не путается с ромбом «ждёт разрешения».</summary>
    public static DotKind ToDot(SzLivenessState s) => s switch
    {
        SzLivenessState.Online => DotKind.Run,
        SzLivenessState.LagSuspected => DotKind.Done,
        SzLivenessState.NoContact => DotKind.Approve,
        _ => DotKind.Err,
    };

    public static DotKind ToDot(SessionState? s) => s switch
    {
        SessionState.Working => DotKind.Run,
        SessionState.WaitingPermission => DotKind.Approve,
        SessionState.AnsweringPeer => DotKind.Ask,
        SessionState.Crashed => DotKind.Err,
        _ => DotKind.Done,
    };

    public static readonly IValueConverter LivenessToDot = new FuncValueConverter<SzLivenessState, DotKind>(ToDot);

    public static readonly IValueConverter LivenessToBrush =
        new FuncValueConverter<SzLivenessState, IBrush>(s => Res(StatusDot.BrushKey(ToDot(s))));

    public static readonly IValueConverter LivenessToText = new FuncValueConverter<SzLivenessState, string>(s => s switch
    {
        SzLivenessState.Online => "онлайн",
        SzLivenessState.LagSuspected => "лаг heartbeat?",
        SzLivenessState.NoContact => "нет связи",
        _ => "откат не завершён",
    });

    public static readonly IValueConverter TransferStateToBrush = new FuncValueConverter<TransferState, IBrush>(s => s switch
    {
        TransferState.Done => Res("Status.Success"),
        TransferState.Failed => Res("Status.Error"),
        _ => Res("Accent.Default"),
    });

    /// <summary>Знак сессии: работает — заливка, ждёт разрешения — ромб, отвечает соседу — кольцо
    /// «ждёт ответа», упала — треугольник, остальное — контур.</summary>
    public static readonly IValueConverter SessionStateToDot = new FuncValueConverter<SessionState?, DotKind>(ToDot);

    public static readonly IValueConverter SessionStateToBrush =
        new FuncValueConverter<SessionState?, IBrush>(s => Res(StatusDot.BrushKey(ToDot(s))));

    /// <summary>Пульсирует только работающая сейчас сессия.</summary>
    public static readonly IValueConverter SessionStateIsLive =
        new FuncValueConverter<SessionState?, bool>(s => s is SessionState.Working);

    public static readonly IValueConverter SessionStateToText = new FuncValueConverter<SessionState?, string>(s => s switch
    {
        SessionState.Working => "Claude работает",
        SessionState.WaitingPermission => "Claude ждёт разрешения",
        SessionState.AnsweringPeer => "Claude отвечает соседней сессии",
        SessionState.Crashed => "сессия упала",
        SessionState.Idle => "сессия готова",
        _ => "сессия остановлена",
    });

    private static Geometry? Icon(string key)
        => Application.Current!.TryGetResource(key, null, out var v) ? v as Geometry : null;

    /// <summary>Исход вызова инструмента (ToolCall): галочка / крестик / запрет / часы.</summary>
    public static readonly IValueConverter ToolStatusToIcon = new FuncValueConverter<ToolStatus, Geometry?>(s => s switch
    {
        ToolStatus.Ok => Icon("Icon.Check"),
        ToolStatus.Error => Icon("Icon.X"),
        ToolStatus.Denied => Icon("Icon.Ban"),
        _ => Icon("Icon.Clock"),
    });

    public static readonly IValueConverter ToolStatusToBrush = new FuncValueConverter<ToolStatus, IBrush>(s => s switch
    {
        ToolStatus.Ok => Res("Status.Success"),
        ToolStatus.Error => Res("Status.Error"),
        ToolStatus.Denied => Res("Text.Disabled"),
        _ => Res("Accent.Text"),
    });

    public static readonly IValueConverter ToolStatusToText = new FuncValueConverter<ToolStatus, string>(s => s switch
    {
        ToolStatus.Ok => "выполнен",
        ToolStatus.Error => "ошибка",
        ToolStatus.Denied => "запрещён",
        _ => "выполняется",
    });

    public static readonly IValueConverter BoolToChevron =
        new FuncValueConverter<bool, Geometry?>(open => Icon(open ? "Icon.ChevDown" : "Icon.ChevRight"));
}
