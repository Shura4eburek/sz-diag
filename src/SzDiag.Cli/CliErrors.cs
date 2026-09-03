namespace SzDiag.Cli;

/// <summary>Единая расшифровка сетевых сбоев для всего CLI.
///
/// Боль (бэклог п.70/78): `szcli sensors status` под нагрузкой вываливал 25 строк
/// `TaskCanceledException`, хотя ситуация штатная — агент задавлен и hub честно отдал 504.
/// Обработка была точечной (только у `exec`/`pull`/`push`), поэтому каждая новая команда
/// приезжала без неё. Здесь — один разбор на весь CLI, подключённый в `Program` поверх
/// всего switch: сообщение в одну строку и ненулевой код возврата вместо стектрейса.</summary>
public static class CliErrors
{
    /// <summary>Сбой, который для оператора — нормальная ситуация (агент занят, hub не поднят),
    /// а не дефект CLI. Такие показываем строкой; остальные пусть падают со стектрейсом.</summary>
    public static bool IsExpected(Exception ex) => ex is TimeoutException
        or TaskCanceledException
        or OperationCanceledException
        or HttpRequestException;

    /// <summary>Человеческое объяснение без стектрейса. `hubUrl` подставляется в сообщение
    /// о недоступном hub — иначе непонятно, куда именно CLI не достучался.</summary>
    public static string Describe(Exception ex, string? hubUrl = null) => ex switch
    {
        TimeoutException t => $"Таймаут: {t.Message}",

        // TaskCanceledException прилетает, когда CancellationTokenSource CLI сработал раньше
        // ответа hub: агент под 100% нагрузкой не отвечает минутами (п.43/п.79).
        TaskCanceledException or OperationCanceledException =>
            "Таймаут: hub не ответил вовремя — агент, скорее всего, задавлен нагрузкой. "
            + "Проверь szcli list (heartbeat/boot-time) и повтори позже.",

        // StatusCode заполнен — значит hub ОТВЕТИЛ (пусть и не тем, что мы ждали): это разные
        // диагнозы с «сеть/hub недоступны» (бэклог п.212, СЗ 161498). 405 от `--result ""`
        // (запрос без jobId) при живом hub раньше читался как «Hub недоступен» и уводил
        // диагностику не туда — hub стоял и отвечал секунда в секунду.
        HttpRequestException { StatusCode: { } code } h =>
            $"Hub ответил {(int)code} {code}: {h.Message} — hub жив, это не «недоступен», а неверный запрос/маршрут CLI.",

        HttpRequestException h =>
            $"Hub недоступен{(string.IsNullOrWhiteSpace(hubUrl) ? "" : $" ({hubUrl})")}: {h.Message}",

        _ => ex.Message,
    };

    /// <summary>Hub ответил (пусть и отказом) — отдельный код от «до hub не достучались».</summary>
    public const int HubRejected = 5;

    /// <summary>Код возврата: всегда ненулевой, чтобы обёртки в скриптах видели сбой.</summary>
    public static int ExitCode(Exception ex) => ex switch
    {
        TimeoutException or TaskCanceledException or OperationCanceledException => 3,
        HttpRequestException { StatusCode: { } } => HubRejected,
        HttpRequestException => 4,
        _ => 1,
    };
}
