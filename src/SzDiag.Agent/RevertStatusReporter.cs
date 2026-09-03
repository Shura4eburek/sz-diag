using System.Net.Http.Json;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Сообщает hub итог `agent.exe --revert` по HTTP (watchdog/headless-откат — в этом
/// режиме нет живого SignalR-коннекта, чтобы ответить обычным путём). Best-effort: сетевой
/// сбой здесь не должен мешать самому откату — он уже случился к моменту вызова.
///
/// Боль (бэклог п.59, СЗ 160705): watchdog сработал, `--revert` упал на середине — доступ
/// остался на клиенте, а `szcli list` продолжал показывать СЗ online, потому что hub об
/// этом ни разу не узнавал.</summary>
public sealed class RevertStatusReporter
{
    private readonly HttpClient _http;

    /// <param name="http">Клиент с BaseAddress = адрес hub и заголовком AgentToken
    /// (тот же, что у апдейтера/push).</param>
    public RevertStatusReporter(HttpClient http) => _http = http;

    /// <summary>Не бросает: любая ошибка сети/hub возвращается строкой, а не исключением —
    /// вызывающий (watchdog-код в Program.cs) не должен зависеть от доступности hub.</summary>
    public async Task<string?> ReportAsync(string sz, bool success, string summary, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
                new RevertStatusReport(sz, success, summary), ct);
            return resp.IsSuccessStatusCode ? null : $"hub ответил {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
