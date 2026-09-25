using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Живые сенсоры из CSV `lhmmon`: последний отсчёт и ломаные температур по хвосту.
/// Синхронный exec раз в 15 с и только пока вкладка открыта: слот синхронного exec у агента
/// один, и чаще опрашивать — значит отбирать его у оператора. Под полной нагрузкой exec может
/// не ответить или прийти «занят» — тогда остаются прежние значения, а статус говорит об этом прямо.</summary>
public sealed partial class SensorsTabViewModel(IHubApiClient api, TimeProvider time)
    : ObservableObject, IInspectorTab
{
    public const int TailRows = 120;
    public const double ChartWidth = 260;
    public const double ChartHeight = 56;
    public const string NoCsvMarker = "SZDIAG_NO_CSV";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private string? _sz;
    private DateTime? _lastSampleTime;
    private DateTimeOffset _lastChangeAt;

    public string Title => "Сенсоры";
    public TimeSpan? Interval => TimeSpan.FromSeconds(15);

    /// <summary>Чем запускать `lhmmon`. Не `szcli sensors start`: тот пишет свой CSV в ProgramData,
    /// а вкладка читает CSV `lhmmon`.</summary>
    public string StartHint => $"запусти lhmmon: szcli exec {_sz ?? "<СЗ>"} -f tools\\recipes\\client\\start-sensors.ps1";

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _notWriting;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _voltText = "—";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCpuLine))] private IReadOnlyList<Point> _cpuTempLine = Array.Empty<Point>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasGpuLine))] private IReadOnlyList<Point> _gpuTempLine = Array.Empty<Point>();

    public bool HasCpuLine => CpuTempLine.Count > 1;
    public bool HasGpuLine => GpuTempLine.Count > 1;

    /// <summary>Шапка + хвост CSV. Шапка нужна парсеру (по ней он узнаёт формат), а в коротком
    /// файле хвост её уже содержит — второй раз не отдаём.</summary>
    internal static string Script => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $p = '{{SensorPaths.LhmCsv}}'
        if (-not (Test-Path $p)) { '{{NoCsvMarker}}'; return }
        $head = Get-Content $p -TotalCount 1
        $tail = @(Get-Content $p -Tail {{TailRows}})
        if ($tail.Count -gt 0 -and $tail[0] -eq $head) { $tail = $tail | Select-Object -Skip 1 }
        $head
        $tail
        """;

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        if (_sz != sz) { _sz = sz; OnPropertyChanged(nameof(StartHint)); }
        ExecResult? r;
        try
        {
            r = await api.ExecAsync(sz, Script, 15, ct);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Status = "агент не ответил за 15 с — под нагрузкой exec глохнет; показаны прошлые данные";
            return;
        }
        catch (HttpRequestException ex)
        {
            Status = $"hub: {ex.Message}";
            return;
        }
        if (r is null)
        {
            Status = "СЗ не на связи";
            return;
        }
        // «Занят» (единственный слот синхронного exec держит другая команда) и таймаут скрипта
        // приходят пустым StdOut — это не «сенсоры не пишутся»: прошлые данные остаются.
        if (r.TimedOut || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.StdOut))
        {
            var why = r.TimedOut ? "не уложился в 15 с" : (string.IsNullOrWhiteSpace(r.StdErr) ? "пустой ответ" : r.StdErr.Trim());
            Status = $"агент не отдал сенсоры ({why}); показаны прошлые данные";
            return;
        }
        if (r.StdOut.Contains(NoCsvMarker, StringComparison.Ordinal))
        {
            NotWriting = true;
            HasData = false;
            Status = $"сенсоры не пишутся: нет {SensorPaths.LhmCsv}";
            return;
        }

        var samples = SensorReport.ParseAny(r.StdOut).Samples;
        if (samples.Count == 0)
        {
            NotWriting = true;
            Status = "сенсоры не пишутся: в CSV нет разборчивых строк";
            return;
        }

        var last = samples[^1];
        var now = time.GetUtcNow();
        if (_lastSampleTime != last.Time)
        {
            _lastSampleTime = last.Time;
            _lastChangeAt = now;
        }
        NotWriting = now - _lastChangeAt >= StaleAfter;
        HasData = true;

        CpuText = $"CPU {F(last.CpuTempC, " °C")} · {F(last.CpuPercent, " %")} · {F(last.CpuClockMhz, " МГц")}";
        GpuText = $"GPU {F(last.GpuTempC, " °C")} · {F(last.GpuPercent, " %")}";
        PowerText = $"мощность CPU {F(last.CpuPowerW, " Вт")} · GPU {F(last.GpuPowerW, " Вт")}";
        VoltText = $"12V {F(last.Volt12, " В", "0.00")} · 5V {F(last.Volt5, " В", "0.00")}";
        CpuTempLine = Sparkline.Points(samples.Select(s => s.CpuTempC).ToList(), ChartWidth, ChartHeight);
        GpuTempLine = Sparkline.Points(samples.Select(s => s.GpuTempC).ToList(), ChartWidth, ChartHeight);
        Status = NotWriting
            ? $"сенсоры не пишутся: последняя строка не менялась {(int)(now - _lastChangeAt).TotalSeconds} с"
            : $"последняя строка {last.Time:HH:mm:ss} (часы клиента), отсчётов в хвосте: {samples.Count}";
    }

    private static string F(double? v, string unit, string format = "0.#")
        => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) + unit : "—";

    public void Clear()
    {
        _sz = null;
        _lastSampleTime = null;
        Status = "";
        NotWriting = false;
        HasData = false;
        CpuText = GpuText = PowerText = VoltText = "—";
        CpuTempLine = Array.Empty<Point>();
        GpuTempLine = Array.Empty<Point>();
    }
}
