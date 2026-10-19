using SzDiag.Contracts;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class TransferItemViewModelTests
{
    private static TransferInfo T(TransferDirection d, long? total, long done, TransferState st, string? note = null)
        => new("r1", "161432", d, "occt", total, done, 17 * 1024 * 1024, DateTimeOffset.UtcNow, st, note);

    [Fact]
    public void Running_WithTotal_PercentAndDetail()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Push, 690L << 20, 412L << 20, TransferState.Running));
        Assert.Equal("push occt → 161432", vm.Title);
        Assert.Equal(59.7, vm.Percent!.Value, precision: 1);
        Assert.Equal("412 / 690 МБ · 17 МБ/с", vm.Detail);
    }

    [Fact]
    public void Running_UnknownTotal_Indeterminate()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Pull, null, 3L << 20, TransferState.Running));
        Assert.Null(vm.Percent);
        Assert.Equal("pull occt ← 161432", vm.Title);
        Assert.Equal("3 МБ · 17 МБ/с", vm.Detail);
    }

    [Fact]
    public void Done_ShowsNote()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Push, 100, 0, TransferState.Done, "скачано 0, пропущено 2"));
        Assert.Equal(100.0, vm.Percent);
        Assert.Equal("готово · скачано 0, пропущено 2", vm.Detail);
    }

    [Fact]
    public void Running_SlowNetwork_ShowsKilobytes()
    {
        // Плохая сеть — ровно тот случай, ради которого прогресс делали: «0 / 300 МБ · 0 МБ/с»
        // выглядел бы зависанием.
        var t = new TransferInfo("r1", "161432", TransferDirection.Push, "occt", 300L << 20, 512L << 10,
            200 * 1024, DateTimeOffset.UtcNow, TransferState.Running);
        Assert.Equal("512 КБ / 300 МБ · 200 КБ/с", new TransferItemViewModel(t).Detail);
    }

    [Fact]
    public void Running_FewMegabytes_OneDecimal()
    {
        var t = new TransferInfo("r1", "161432", TransferDirection.Pull, "dmp", null, (long)(2.5 * (1 << 20)),
            1.5 * (1 << 20), DateTimeOffset.UtcNow, TransferState.Running);
        Assert.Equal("2.5 МБ · 1.5 МБ/с", new TransferItemViewModel(t).Detail);
    }

    [Fact]
    public void Failed_ShowsError()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Pull, null, 0, TransferState.Failed, "таймаут"));
        Assert.Equal("ошибка: таймаут", vm.Detail);
    }
}
