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

        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {sz}: Windows Update заморожен[/] (службы {string.Join(", ", WindowsUpdateFreeze.Services)} + политика WSUS).");
        AnsiConsole.MarkupLineInterpolated($"[grey]Прежние значения:[/] {path}");
        AnsiConsole.MarkupLine("[yellow]Не забудь szcli unfreeze перед отдачей машины клиенту.[/]");
        return 0;
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
