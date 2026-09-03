namespace SzDiag.Cli;

/// <summary>Проверки перед походом в hub для `exec --result`/`--cancel`/`--detach`:
/// недостающий jobId — ошибка на стороне CLI, а не сетевой сбой.
///
/// Боль (бэклог п.212, СЗ 161498): `--detach` под полной нагрузкой (`PowerSupply`) не
/// вернул jobId, и следующий `szcli exec --result ""` ушёл в hub как запрос без
/// идентификатора — hub ответил 405 (Method Not Allowed), а CLI отрендерил это как
/// «Hub недоступен», хотя hub был полностью жив. Обе половины проверяем ДО сетевого
/// вызова: пустой jobId — это «не передан jobId», а не сбой транспорта.</summary>
public static class ExecResultGuard
{
    /// <summary>jobId для `--result`/`--cancel` пуст — запрос не должен уходить в hub.</summary>
    public static bool IsMissingJobId(string? jobId) => string.IsNullOrWhiteSpace(jobId);

    /// <summary>`--detach` обязан вернуть JobId; пустой/отсутствующий — явная ошибка агента,
    /// а не пустая строка, уходящая дальше по конвейеру (issue #163, п.3).</summary>
    public static bool DetachMissingJobId(bool detach, string? jobId)
        => detach && string.IsNullOrEmpty(jobId);
}
