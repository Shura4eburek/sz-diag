namespace SzDiag.Contracts;

/// <param name="Step">Что откатывали (имя шага).</param>
/// <param name="Error">Полный текст исключения.</param>
public sealed record RevertResultFailure(string Step, string Error);

/// <summary>Итог отката, который агент шлёт hub'у ДО отключения канала.
///
/// Боль (бэклог п.119): агент к моменту `close` может быть уже офлайн (закрылся ярлыком, или
/// откат случился до вызова hub), и «Проверить остатки на клиенте (пока агент жив): szcli
/// client info» превращается в невыполнимый совет — единственный канал уже закрыт вместе
/// с доступом. Раньше `Revert()` на клиенте считал итог (<c>Done</c>/<c>Failed</c>), но
/// никуда его не отправлял — сводка терялась вместе с процессом агента. Теперь она уходит
/// на hub первым делом после отката, пока канал ещё жив, и `close` может напечатать её
/// вместо гадания.</summary>
public sealed record RevertResult(string Sz, IReadOnlyList<string> Done, IReadOnlyList<RevertResultFailure> Failed)
{
    public bool AllClean => Failed.Count == 0;
}

/// <summary>Ответ hub на `close`: закрылась ли сессия, и если агент успел прислать итог
/// отката до отключения канала — какой он.</summary>
public sealed record CloseOutcome(bool Closed, RevertResult? Revert);
