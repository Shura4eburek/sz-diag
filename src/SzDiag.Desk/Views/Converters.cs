using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Views;

public static class Converters
{
    private static IBrush Res(string key)
        => Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

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
}
