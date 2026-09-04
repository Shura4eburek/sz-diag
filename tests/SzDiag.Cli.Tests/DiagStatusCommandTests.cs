using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Бэклог п.6 (СЗ 160306): диагностика не дала отчёта, а CLI до самого конца отвечал
/// «диагностика запущена» — упавший прогон выглядел как пустая папка, и оператор ждал вслепую
/// 20 минут, потом собирал всё руками по SSH. `szcli diag status` обязан сразу назвать причину.</summary>
public class DiagStatusCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(string activity = "", DateTimeOffset? since = null)
        => new("160306", "10.0.0.5", "PC-1", SessionStatus.Online, Now, Now, activity, since);

    [Fact]
    public void UnknownSession_FailsWithNotFound()
    {
        var v = DiagStatusCommand.BuildVerdict(session: null, latest: null, Now);

        Assert.Equal(1, v.Code);
        Assert.Contains("не найдена", v.Markup);
    }

    [Fact]
    public void FailedActivity_ReportsFailureNotSuccess()
    {
        // Ровно текст, который агент шлёт на исключение в RunAndUploadAsync.
        var v = DiagStatusCommand.BuildVerdict(
            Session("готов · диагностика: ошибка ⚠"), latest: null, Now);

        Assert.Equal(1, v.Code);
        Assert.Contains("упала", v.Markup);
    }

    [Fact]
    public void RunningActivity_ShowsCurrentStepAndElapsed()
    {
        var since = Now - TimeSpan.FromSeconds(90);
        var v = DiagStatusCommand.BuildVerdict(
            Session("диагностика: reliability", since), latest: null, Now);

        Assert.Equal(0, v.Code);
        Assert.Contains("идёт", v.Markup);
        Assert.Contains("reliability", v.Markup);
        Assert.Contains("1мин 30сек", v.Markup);
    }

    [Fact]
    public void NoActivityYet_SaysSoInsteadOfBlank()
    {
        var v = DiagStatusCommand.BuildVerdict(Session(), latest: null, Now);

        Assert.Equal(0, v.Code);
        Assert.Contains("не запускали", v.Markup);
    }

    [Fact]
    public void LatestReport_IsIncludedInOutput()
    {
        var v = DiagStatusCommand.BuildVerdict(
            Session("готов · диагностика: полная диагностика"),
            latest: (@"C:\kb\СЗ\160306\reports\20260904-120000\diag.md", 4096), Now);

        Assert.Contains("diag.md", v.Markup);
        Assert.Contains("4,0", v.Markup.Replace('.', ','));
    }

    [Fact]
    public void NoReportYet_SaysSo()
    {
        var v = DiagStatusCommand.BuildVerdict(Session("готов · диагностика: ошибка ⚠"), latest: null, Now);

        Assert.Contains("Отчётов ещё не было", v.Markup);
    }
}
