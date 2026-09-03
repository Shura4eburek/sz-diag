namespace SzDiag.Contracts;

/// <summary>Агент → hub: итог `agent.exe --revert` (watchdog или headless-откат), отправляется
/// по HTTP — в этом режиме нет живого SignalR-коннекта, чтобы ответить обычным путём.
///
/// Боль (бэклог п.59, СЗ 160705): watchdog сработал, `--revert` упал на середине —
/// доступ (sshd, учётка, фаервол) остался на клиенте навсегда, а hub ни разу об этом не
/// узнал: `szcli list` продолжал показывать СЗ так, будто всё в порядке.</summary>
/// <param name="Sz">Номер сервисной заявки.</param>
/// <param name="Success">Откат завершился чисто (`RevertOutcome.AllClean`).</param>
/// <param name="Summary">Короткая сводка (`RevertOutcome.Summary()`) — что не откатилось.</param>
public sealed record RevertStatusReport(string Sz, bool Success, string Summary);
