using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Переподъём приборного захвата (`szcli sensors start`) после ребута клиента.
///
/// Боль (бэклог п.147, СЗ 160705): после hard-off и подъёма машины `lhmmon` не стартовал —
/// сам наблюдатель это обычный дочерний процесс агента, и падает вместе со всей машиной.
/// Агент при этом восстанавливается сам (`--resume` + автостарт-таск), то есть управление
/// возвращается, а приборка — нет. Если бы повторный отказ случился раньше, чем оператор
/// вручную поднял сенсоры, второй эпизод остался бы вообще без данных.
///
/// Ряд пишется в НОВЫЙ файл с меткой времени (тот же принцип, что и у обычного `sensors
/// start` — второй прогон физически не может затереть первый, бэклог п.145), поэтому стык
/// до/после ребута виден в имени файла, а не склеивается.</summary>
public static class SensorCaptureGuard
{
    /// <summary>Есть ли на клиенте маркер незакрытого приборного захвата.</summary>
    public static bool IsMarked() => File.Exists(SensorResumeMarker.MarkerPath);

    /// <summary>Поднять наблюдатель заново, если маркер на месте. Возвращает описание
    /// сделанного (null — маркера нет, поднимать нечего).</summary>
    /// <param name="handle">Обычно <c>ExecCommandHandler.Handle</c> — тот же путь, которым
    /// hub поднимает наблюдатель штатно, поэтому последующие `sensors status`/`stop`
    /// увидят job как любой другой.</param>
    /// <param name="isMarked">Тест-шов; по умолчанию — маркер на диске.</param>
    /// <param name="readMarker">Тест-шов; по умолчанию — прочитать и разобрать файл маркера.</param>
    public static string? ResumeIfMarked(Func<ExecRequest, ExecResult> handle,
        Func<bool>? isMarked = null, Func<SensorResumeMarker.Info?>? readMarker = null)
    {
        var marked = isMarked ?? IsMarked;
        if (!marked()) return null;

        var read = readMarker ?? (() =>
        {
            try { return SensorResumeMarker.Parse(File.ReadAllText(SensorResumeMarker.MarkerPath)); }
            catch (IOException) { return null; }
        });
        var info = read();
        if (info is null) return "маркер приборного захвата битый — наблюдатель не поднят";

        var csvPath = $@"C:\ProgramData\szdiag\sensors\{info.Sz}-{DateTime.Now:yyyyMMdd-HHmmss}-resumed.csv";
        var script = "New-Item -ItemType Directory -Force -Path (Split-Path '" +
                     csvPath.Replace("'", "''") + "') | Out-Null\n" +
                     SensorWatcher.BuildScript(csvPath, info.IntervalSeconds, info.Minutes,
                         ClientTraces.StressProcessNames);

        var result = handle(new ExecRequest(info.Sz, Guid.NewGuid().ToString("N"), script, 60, Detached: true));
        return result.ExitCode == 0
            ? $"приборный захват поднят заново после ребута (СЗ {info.Sz}): {csvPath}"
            : $"приборный захват НЕ поднят после ребута: {result.StdErr}";
    }
}
