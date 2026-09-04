using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Бэклог п.147 (#86, СЗ 160705): после hard-off `lhmmon` не стартовал сам — задача
/// была `/sc once`, автозапуска нет. Агент восстанавливает управление (`--resume`), но не
/// приборный захват. `SensorResumeMarker`/`SensorCaptureGuard` дают агенту всё нужное, чтобы
/// поднять наблюдатель заново без похода к hub.</summary>
public class SensorCaptureGuardTests
{
    [Fact]
    public void Marker_RoundTripsThroughWriteAndParseScripts()
    {
        // BuildWriteScript генерит PS, а не JSON напрямую — сверяем, что то, что реально
        // попадёт в файл (Set-Content -Value '...'), Parse потом успешно читает обратно.
        var script = SensorResumeMarker.BuildWriteScript("160705", 10, 240);

        var start = script.IndexOf("-Value '", StringComparison.Ordinal) + "-Value '".Length;
        var end = script.IndexOf("' -Encoding", start, StringComparison.Ordinal);
        var json = script[start..end];

        var info = SensorResumeMarker.Parse(json);

        Assert.NotNull(info);
        Assert.Equal("160705", info!.Sz);
        Assert.Equal(10, info.IntervalSeconds);
        Assert.Equal(240, info.Minutes);
    }

    [Fact]
    public void Marker_Parse_GarbageInput_ReturnsNullNotThrows()
        => Assert.Null(SensorResumeMarker.Parse("это не json вообще"));

    [Fact]
    public void RemoveScript_TargetsTheSameMarkerPath()
    {
        var write = SensorResumeMarker.BuildWriteScript("160705", 10, 240);
        var remove = SensorResumeMarker.BuildRemoveScript();

        Assert.Contains("resume-marker.json", write);
        Assert.Contains("resume-marker.json", remove);
        Assert.Contains("Remove-Item", remove);
    }

    [Fact]
    public void ResumeIfMarked_NoMarker_ReturnsNull_DoesNotStartAnything()
    {
        var started = false;
        Func<ExecRequest, ExecResult> handle = req => { started = true; return new ExecResult(req.RequestId, 0, "", ""); };

        var result = SensorCaptureGuard.ResumeIfMarked(handle, isMarked: () => false);

        Assert.Null(result);
        Assert.False(started);
    }

    [Fact]
    public void ResumeIfMarked_MarkerPresent_StartsWatcherForRememberedSzAndInterval()
    {
        ExecRequest? captured = null;
        Func<ExecRequest, ExecResult> handle = req => { captured = req; return new ExecResult(req.RequestId, 0, "", ""); };

        var result = SensorCaptureGuard.ResumeIfMarked(handle,
            isMarked: () => true,
            readMarker: () => new SensorResumeMarker.Info("160705", 10, 240));

        Assert.NotNull(result);
        Assert.Contains("160705", result);
        Assert.NotNull(captured);
        Assert.Equal("160705", captured!.Sz);
        Assert.True(captured.Detached);
        Assert.Contains("cpu_pct", captured.Script);   // тело SensorWatcher.BuildScript
        // Новый файл с меткой времени (п.145) — старый CSV не трогаем и не дописываем в него,
        // чтобы стык до/после ребута был виден по имени файла, а не склеен.
        Assert.Contains("resumed", captured.Script);
        Assert.Contains("160705", captured.Script);
    }

    [Fact]
    public void ResumeIfMarked_BrokenMarker_ReturnsDescriptiveNote_DoesNotThrow()
    {
        Func<ExecRequest, ExecResult> handle = req => new ExecResult(req.RequestId, 0, "", "");

        var result = SensorCaptureGuard.ResumeIfMarked(handle,
            isMarked: () => true,
            readMarker: () => null);

        Assert.NotNull(result);
        Assert.Contains("битый", result);
    }

    [Fact]
    public void ResumeIfMarked_HandlerFails_ReportsFailureNotSilence()
    {
        Func<ExecRequest, ExecResult> handle = req => new ExecResult(req.RequestId, -1, "", "не удалось");

        var result = SensorCaptureGuard.ResumeIfMarked(handle,
            isMarked: () => true,
            readMarker: () => new SensorResumeMarker.Info("160705", 10, 240));

        Assert.NotNull(result);
        Assert.Contains("НЕ поднят", result);
    }
}
