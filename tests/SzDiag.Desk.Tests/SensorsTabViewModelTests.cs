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
        => new(new FakeHubApi { Exec = exec }, _szcli, _clock);

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
    public async Task NoCsv_NotWriting_OffersStart()
    {
        var vm = New((_, _) => Out(SensorsTabViewModel.NoCsvMarker));
        await vm.RefreshAsync("161432", default);
        Assert.True(vm.NotWriting);
        Assert.False(vm.HasData);
        Assert.Contains("сенсоры не пишутся", vm.Status);

        await vm.StartSensorsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "sensors", "start", "161432" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task SameLastRowFor30sOfHostTime_NotWriting()
    {
        // Время строк — по часам клиента (в WinPE сдвинуто на пояс): «не пишутся» считаем по
        // часам бокса — последняя строка не менялась 30 с.
        var csv = Csv(("2026-09-25 03:00:00", 70, 60));
        var vm = New((_, _) => Out(csv));
        await vm.RefreshAsync("161432", default);
        Assert.False(vm.NotWriting);

        _clock.Advance(TimeSpan.FromSeconds(31));
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
