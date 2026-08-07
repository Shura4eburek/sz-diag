namespace SzDiag.Contracts;

/// <summary>Payload регистрации агента. IP берётся из соединения, не из payload.
/// <paramref name="BootTime"/> — время загрузки ОС клиента: hub по нему отличает реальный
/// ребут (boot-time сменился) от лага heartbeat под нагрузкой (boot-time тот же). Nullable,
/// т.к. агенты старых сборок поле не шлют; читается один раз при старте агента и не меняется,
/// пока машина не перезагрузилась — поэтому в heartbeat его гонять не нужно.</summary>
/// <param name="LastShutdown">Чем закончилась ПРОШЛАЯ сессия ОС (<see cref="ShutdownKind"/>):
/// hub по нему отличает настоящий обрыв питания от выключения кнопкой. Без этого «выключили
/// кнопкой» и «оборвалось питание» падали в один счётчик вырубонов (бэклог п.93).</param>
public sealed record RegisterRequest(string Sz, string Hostname, DateTimeOffset? BootTime = null,
    string? LastShutdown = null);

/// <summary>Как завершилась прошлая сессия ОС. Строки, а не enum: значение ездит по SignalR и
/// лежит в SQLite, а агенты старых сборок его вообще не шлют.</summary>
public static class ShutdownKind
{
    /// <summary>Жёсткий обрыв: Kernel-Power 41 без BSOD и без нажатия кнопки. Это дефект.</summary>
    public const string HardOff = "hard-off";

    /// <summary>Выключение кнопкой питания (PowerButtonTimestamp != 0) — НЕ дефект.</summary>
    public const string PowerButton = "button";

    /// <summary>Синий экран (BugcheckCode != 0).</summary>
    public const string Bsod = "bsod";

    /// <summary>Штатное завершение работы — событий 41 по этой загрузке нет.</summary>
    public const string Clean = "clean";

    /// <summary>Определить не удалось (нет прав, журнал недоступен, агент старой сборки).</summary>
    public const string Unknown = "unknown";

    /// <summary>Событие попало в окно обслуживания: с машиной в этот момент работали руками
    /// (бэклог п.100). Дефектом не считается.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>Считать ли событие вырубоном для счётчика `⚡N`. Кнопка и штатное выключение —
    /// не считаются; неизвестное считается (лучше лишний вопрос, чем пропущенный дефект).</summary>
    public static bool CountsAsFailure(string? kind)
        => kind is not (PowerButton or Clean or Maintenance);

    /// <summary>Человеческая подпись для CLI.</summary>
    public static string Describe(string? kind) => kind switch
    {
        HardOff => "обрыв питания",
        PowerButton => "кнопка питания",
        Bsod => "BSOD",
        Clean => "штатно",
        Maintenance => "обслуживание",
        _ => "неизвестно",
    };
}
