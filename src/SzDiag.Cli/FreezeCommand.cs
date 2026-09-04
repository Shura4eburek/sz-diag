using System.Text.Json;
using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli freeze <СЗ>` / `szcli unfreeze <СЗ>` — заморозка Windows Update на время
/// сессии и парное снятие.
///
/// Работает поверх обычного `exec`: отдельного протокола не нужно, а прежние значения
/// (что было до нас) складываются **на хосте**, рядом с szcli — клиент их потерять не может,
/// и после закрытия СЗ видно, что осталось незакрытым.
///
/// Почему это вообще нужно: на 160636 машина, которую только что вытащили из кирпича от
/// сорванного обновления, за четыре часа сама завела себе новую транзакцию на 46,5 МБ
/// (бэклог п.34b).</summary>
public static class FreezeCommand
{
    /// <summary>Заморозка требует времени на остановку служб — стандартных 120 с мало,
    /// когда машина под нагрузкой.</summary>
    private const int TimeoutSeconds = 300;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Файл с прежними значениями для конкретной СЗ.</summary>
    public static string StatePath(string stateDir, string sz)
        => Path.Combine(stateDir, "freeze", $"{sz}.json");

    /// <summary>Есть ли незакрытая заморозка по этой СЗ.</summary>
    public static bool IsFrozen(string stateDir, string sz) => File.Exists(StatePath(stateDir, sz));

    public static async Task<int> FreezeAsync(IHubApiClient client, string sz, string stateDir)
    {
        var capture = await client.ExecAsync(sz, WindowsUpdateFreeze.BuildCaptureScript(), TimeoutSeconds);
        if (capture is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        // Машину, которая уже в середине применения пакета, замораживать опасно: оборванная
        // транзакция — это ровно тот способ, которым 160636 дважды убили (бэклог п.35b).
        if (WindowsUpdateFreeze.HasPendingTransaction(capture.StdOut))
            AnsiConsole.MarkupLine("[yellow]⚠ На машине есть pending.xml — идёт незавершённая транзакция обновления.[/]");

        var previous = WindowsUpdateFreeze.ParseCapture(capture.StdOut);
        var path = StatePath(stateDir, sz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Сохраняем ДО применения: если заморозка упадёт на середине, вернуть всё равно есть чем.
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(previous, Json));

        var result = await client.ExecAsync(sz, WindowsUpdateFreeze.BuildFreezeScript(), TimeoutSeconds);
        if (result is null || result.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Заморозка не удалась:[/] {result?.StdErr ?? "агент не ответил"}");
            return 1;
        }

        // Не рапортуем об успехе по факту записи в реестр: на 160306 всё «применилось», а
        // после ребута wuauserv оказался живым (бэклог п.72). Перечитываем фактическое
        // состояние и говорим правду.
        var check = await VerifyAsync(client, sz);
        if (check is null)
        {
            AnsiConsole.MarkupLine("[yellow]Заморозка применена, но проверить состояние не удалось (агент не ответил).[/]");
            return 1;
        }

        var services = string.Join(", ", WindowsUpdateFreeze.Services);
        // Бэклог п.150 (СЗ 161716): первая строка рапортовала «заморожен» БЕЗУСЛОВНО, а
        // предупреждение про неполное применение шло ниже — в логе это читалось как успех.
        // Заголовок теперь сам зависит от факта защиты, а не от факта записи в реестр.
        if (!check.IsProtected)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]СЗ {sz}: Windows Update НЕ заморожен полностью[/] — машина не защищена от WU-перезагрузки.");
            AnsiConsole.MarkupLine("[red]Не применилось:[/]");
            foreach (var p in check.Blocking) AnsiConsole.MarkupLineInterpolated($"  [red]•[/] {p}");
            AnsiConsole.MarkupLine("[yellow]Проверь права и повтори; после ребута состояние сверяется само (агент).[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[green]СЗ {sz}: Windows Update заморожен[/] (службы {services}, политика WSUS, задачи оркестратора выключены).");
            AnsiConsole.MarkupLine("[grey]Проверено фактическое состояние: службы и политика на месте.[/]");
            foreach (var p in check.Cosmetic)
                AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ не критично (службы стоят):[/] {p}");
        }
        AnsiConsole.MarkupLineInterpolated($"[grey]Прежние значения:[/] {path}");
        AnsiConsole.MarkupLine("[yellow]Не забудь szcli unfreeze перед отдачей машины клиенту.[/]");
        // Exit code отвечает на вопрос «машина защищена от WU-перезагрузки?», а не «всё ли
        // из списка применилось»: задачи оркестратора под TrustedInstaller правам не
        // поддаются, но при стоящих службах они безвредны (бэклог п.175).
        return check.IsProtected ? 0 : 1;
    }

    /// <summary>Чистое решение по `--status`: код возврата и вердикт. Вынесено из
    /// <see cref="StatusAsync"/> ради тестов. Нет ни файла на хосте, ни маркера на клиенте —
    /// заморозки нет и не должно быть: это штатное состояние, а не повод кричать
    /// «НЕ полная» и советовать заморозить обратно (бэклог п.115).</summary>
    public static (int Code, string Verdict) StatusVerdict(FreezeCheck check,
        bool hostStateExists, bool clientMarker)
    {
        if (!hostStateExists && !clientMarker)
            return (0, "заморозка не ставилась (или снята) — состояние штатное");
        if (check.IsProtected)
            return (0, check.Cosmetic.Count > 0
                ? "заморозка держится (задачи оркестратора живы, но службы стоят — не критично)"
                : "заморозка держится");
        return (1, "заморозка НЕ полная — машина не защищена от WU-перезагрузки");
    }

    /// <summary>`szcli freeze --status <СЗ>` — что на машине на самом деле, без ручного
    /// `exec` в реестр. Главный вопрос после ребута: заморозка ещё держится?</summary>
    public static async Task<int> StatusAsync(IHubApiClient client, string sz, string stateDir)
    {
        var verify = await client.ExecAsync(sz, WindowsUpdateFreeze.BuildVerifyScript(), TimeoutSeconds);
        if (verify is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        var values = WindowsUpdateFreeze.ParseCapture(verify.StdOut);
        foreach (var svc in WindowsUpdateFreeze.Services)
        {
            values.TryGetValue($"svc:{svc}", out var start);
            values.TryGetValue($"state:{svc}", out var state);
            AnsiConsole.MarkupLineInterpolated($"  {svc,-14} Start={start,-3} {state}");
        }
        AnsiConsole.MarkupLineInterpolated(
            $"  WUServer={values.GetValueOrDefault("pol:WUServer")}  NoAutoUpdate={values.GetValueOrDefault("pol:NoAutoUpdate")}");

        var check = WindowsUpdateFreeze.CheckAppliedDetailed(verify.StdOut);
        var hasState = IsFrozen(stateDir, sz);
        var clientMarker = WindowsUpdateFreeze.HasClientMarker(verify.StdOut);
        var (code, verdict) = StatusVerdict(check, hasState, clientMarker);

        if (code == 0 && check.IsProtected && (hasState || clientMarker))
        {
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: {verdict}.[/]");
            foreach (var p in check.Cosmetic) AnsiConsole.MarkupLineInterpolated($"  [yellow]•[/] {p}");
            if (!hasState)
                AnsiConsole.MarkupLine("[yellow]Но файла с прежними значениями на хосте нет — unfreeze вернёт службы в Manual.[/]");
            return 0;
        }
        if (code == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: {verdict}.[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz}: {verdict}:[/]");
        foreach (var p in check.Blocking) AnsiConsole.MarkupLineInterpolated($"  [red]•[/] {p}");
        foreach (var p in check.Cosmetic) AnsiConsole.MarkupLineInterpolated($"  [yellow]•[/] {p}");
        AnsiConsole.MarkupLineInterpolated($"[grey]Починить:[/] szcli freeze {sz}");
        return 1;
    }

    /// <summary>Фактическое состояние после применения. null — агент не ответил.</summary>
    private static async Task<FreezeCheck?> VerifyAsync(IHubApiClient client, string sz)
    {
        var verify = await client.ExecAsync(sz, WindowsUpdateFreeze.BuildVerifyScript(), TimeoutSeconds);
        return verify is null ? null : WindowsUpdateFreeze.CheckAppliedDetailed(verify.StdOut);
    }

    public static async Task<int> UnfreezeAsync(IHubApiClient client, string sz, string stateDir)
    {
        var path = StatePath(stateDir, sz);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path));
            if (saved is not null) previous = new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Файла нет (замораживали с другой машины/потеряли) — разморозить всё равно надо:
            // скрипт вернёт службы в Manual и снимет политику.
            AnsiConsole.MarkupLine("[yellow]Прежние значения не найдены — вернём службы в Manual и снимем политику.[/]");
        }

        var result = await client.ExecAsync(sz, WindowsUpdateFreeze.BuildUnfreezeScript(previous), TimeoutSeconds);
        if (result is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Разморозка не удалась:[/] {result.StdErr}");
            return 1;
        }

        if (File.Exists(path)) File.Delete(path);
        AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: Windows Update разморожен[/] (прежние значения возвращены).");
        return 0;
    }

    /// <summary>Предупреждение при закрытии СЗ: машина не должна уехать к клиенту с
    /// замороженными обновлениями безопасности.</summary>
    public static void WarnIfStillFrozen(string stateDir, string sz)
    {
        if (!IsFrozen(stateDir, sz)) return;
        AnsiConsole.MarkupLineInterpolated(
            $"[red]⚠ На СЗ {sz} остался заморожен Windows Update![/] Сними до отдачи машины: szcli unfreeze {sz}");
    }
}
