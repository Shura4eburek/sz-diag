using SzDiag.Contracts;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class SensorsTabViewModelTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeSzcliRunner _szcli = new();

    // Формат лёгкого наблюдателя: time;cpu%;стресс;cpu°C;ram%;gpu%;gpu°C;gpu W;cpu МГц.
    private static string Csv(params (string Time, double CpuTemp, double GpuTemp)[] rows)
        => "time;cpu;stress;cputemp;ram;gpu;gputemp;gpuw;clock\n" + string.Join("\n",
            rows.Select(r => $"{r.Time};95;2;{r.CpuTemp.ToString(System.Globalization.CultureInfo.InvariantCulture)};40;97;{r.GpuTemp.ToString(System.Globalization.CultureInfo.InvariantCulture)};180;4500"));

    private static ExecResult Out(string stdout) => new("r", 0, stdout, "");

    private SensorsTabViewModel New(Func<string, string, ExecResult?> exec)
        => new(new FakeHubApi { Exec = exec }, _clock);

    [Fact]
    public async Task Refresh_LatestValues_AndCharts()
    {
        var vm = New((_, script) =>
        {
            Assert.Contains(SensorPaths.LhmCsv, script);
            return Out(Csv(("2026-09-25 12:00:00", 70, 60), ("2026-09-25 12:00:01", 72.5, 61)));
        });
        await vm.RefreshAsync("161432", default);

        Assert.True(vm.HasData);
        Assert.False(vm.NotWriting);
        Assert.Contains("72.5 °C", vm.CpuText);
        Assert.Contains("61 °C", vm.GpuText);
        Assert.Contains("180 Вт", vm.PowerText);
        Assert.Equal(2, vm.CpuTempLine.Count);
        Assert.True(vm.HasCpuLine);
    }

    [Fact]
    public void Script_NewestOfLhmmonAndLightObserver()
    {
        // Живая заявка 160176: Claude запустил `szcli sensors start` (CSV в ProgramData), а вкладка
        // читала только CSV lhmmon и писала «не пишутся». Читаем свежайший из двух.
        var s = SensorsTabViewModel.ScriptFor("160176");
        Assert.Contains(SensorPaths.LhmCsv, s);
        Assert.Contains(SensorPaths.LightDir, s);
        Assert.Contains("160176-*.csv", s);
    }

    [Fact]
    public async Task Source_ShownInStatus()
    {
        var vm = New((_, _) => Out($"{SensorsTabViewModel.SourceMarker} C:\\ProgramData\\szdiag\\sensors\\160176-20260925-192555.csv\n"
                                   + Csv(("2026-09-25 19:26:00", 70, 60))));
        await vm.RefreshAsync("160176", default);
        Assert.True(vm.HasData);
        Assert.Contains("160176-20260925-192555.csv", vm.Status);
    }

    [Fact]
    public async Task NoCsv_NotWriting_SaysHowToStartLhmmon()
    {
        // Ревью I-2: `szcli sensors start` пишет свой CSV в ProgramData, а вкладка читает lhmmon —
        // кнопка запускала не тот наблюдатель. Вместо неё — подсказка, чем запускать lhmmon.
        var vm = New((_, _) => Out(SensorsTabViewModel.NoCsvMarker));
        await vm.RefreshAsync("161432", default);
        Assert.True(vm.NotWriting);
        Assert.False(vm.HasData);
        Assert.Contains("сенсоры не пишутся", vm.Status);
        Assert.Contains("start-sensors.ps1", vm.StartHint);
    }

    [Theory]
    [InlineData(-1, false, "агент уже выполняет команду")]
    [InlineData(0, true, "")]
    public async Task AgentBusyOrTimedOut_KeepsData_NotStale(int exitCode, bool timedOut, string stderr)
    {
        // Ревью I-1: у агента один слот синхронного exec — «занят» и таймаут приходят пустым
        // StdOut, и это не «сенсоры не пишутся».
        var busy = false;
        var vm = New((_, _) => busy
            ? new ExecResult("r", exitCode, "", stderr, TimedOut: timedOut)
            : Out(Csv(("2026-09-25 12:00:00", 70, 60))));
        await vm.RefreshAsync("161432", default);
        busy = true;
        await vm.RefreshAsync("161432", default);

        Assert.True(vm.HasData);
        Assert.False(vm.NotWriting);
        Assert.Contains("70 °C", vm.CpuText);
        Assert.Contains("агент", vm.Status);
    }

    [Fact]
    public void Interval_15s_SparesSyncExecSlot()
        => Assert.Equal(TimeSpan.FromSeconds(15), ((IInspectorTab)New((_, _) => null)).Interval);

    [Fact]
    public async Task SameLastRowFor35sOfHostTime_NotWriting()
    {
        // Время строк — по часам клиента (в WinPE сдвинуто на пояс): «не пишутся» считаем по
        // часам бокса — последняя строка не менялась 30 с.
        var csv = Csv(("2026-09-25 03:00:00", 70, 60));
        var vm = New((_, _) => Out(csv));
        await vm.RefreshAsync("161432", default);
        Assert.False(vm.NotWriting);

        _clock.Advance(TimeSpan.FromSeconds(36));   // лёгкий наблюдатель пишет раз в 10 с
        await vm.RefreshAsync("161432", default);
        Assert.True(vm.NotWriting);
        Assert.True(vm.HasData);

        csv = Csv(("2026-09-25 03:00:00", 70, 60), ("2026-09-25 03:00:31", 71, 60));
        await vm.RefreshAsync("161432", default);
        Assert.False(vm.NotWriting);
    }

    [Fact]
    public async Task Timeout_KeepsValuesAndSaysSo()
    {
        var fail = false;
        var vm = New((_, _) => fail ? throw new TimeoutException("нет ответа") : Out(Csv(("2026-09-25 12:00:00", 70, 60))));
        await vm.RefreshAsync("161432", default);
        fail = true;
        await vm.RefreshAsync("161432", default);

        Assert.True(vm.HasData);
        Assert.Contains("70 °C", vm.CpuText);
        Assert.Contains("агент не ответил", vm.Status);
    }

    [Fact]
    public async Task ClientTimeout_TaskCanceled_SaysAgentSilent()
    {
        var vm = New((_, _) => throw new TaskCanceledException());
        await vm.RefreshAsync("161432", default);
        Assert.Contains("агент не ответил", vm.Status);
    }

    [Fact]
    public void Sparkline_ScalesToBox_SkipsGaps()
    {
        var pts = Sparkline.Points(new double?[] { 10, null, 20, 30 }, 100, 50);
        Assert.Equal(3, pts.Count);
        Assert.Equal(0, pts[0].X);
        Assert.Equal(50, pts[0].Y);      // минимум — внизу
        Assert.Equal(100, pts[2].X);
        Assert.Equal(0, pts[2].Y);       // максимум — вверху
        Assert.Empty(Sparkline.Points(new double?[] { 5 }, 100, 50));   // из одной точки линии нет
    }
}
