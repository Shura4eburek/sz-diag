namespace SzDiag.Contracts;

/// <summary>Payload регистрации агента. IP берётся из соединения, не из payload.
/// <paramref name="BootTime"/> — время загрузки ОС клиента: hub по нему отличает реальный
/// ребут (boot-time сменился) от лага heartbeat под нагрузкой (boot-time тот же). Nullable,
/// т.к. агенты старых сборок поле не шлют; читается один раз при старте агента и не меняется,
/// пока машина не перезагрузилась — поэтому в heartbeat его гонять не нужно.</summary>
/// <param name="LastShutdown">Чем закончилась ПРОШЛАЯ сессия ОС (<see cref="ShutdownKind"/>):
/// hub по нему отличает настоящий обрыв питания от выключения кнопкой. Без этого «выключили
/// кнопкой» и «оборвалось питание» падали в один счётчик вырубонов (бэклог п.93).</param>
/// <param name="AgentUser">Под кем работает агент (`WindowsIdentity.GetCurrent().Name`) —
/// `NT AUTHORITY\СИСТЕМА` после автостарт-задачи или `<машина>\<юзер>` при ручном запуске.
/// Меняет, что вообще возможно: из session 0 GUI-операции ломаются молча (бэклог п.220,
/// СЗ 123123 — `setup.exe` мгновенно исчезал, снимок экрана падал, и получаса ушло на
/// версии «UAC» и «несовместимость», пока `whoami` не показал СИСТЕМА).</param>
/// <param name="AgentSessionId">Сессия Windows, в которой живёт процесс агента. 0 — служебная
/// сессия без рабочего стола (никто не увидит открытое окно).</param>
public sealed record RegisterRequest(string Sz, string Hostname, DateTimeOffset? BootTime = null,
    string? LastShutdown = null, string? AgentUser = null, int? AgentSessionId = null);

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

    /// <summary>Плановое обесточивание сервиса (рубильник на ночь): ребут вне рабочих часов
    /// либо совпал с массовой одновременной пропажей heartbeat у нескольких СЗ разом
    /// (бэклог п.130). Дефектом не считается — иначе рубильник искажает счётчик ⚡.</summary>
    public const string PlannedOutage = "planned-outage";

    /// <summary>Машина уснула (S3) и потом проснулась — Kernel-Power 42 → 107. Не вырубон и не
    /// перезагрузка (bootTime не меняется), но пропущенный сон искажает наработку: сутки
    /// «наблюдения» оказывались 7 часами реальной работы (бэклог п.140/222).</summary>
    public const string Sleep = "sleep";

    /// <summary>Считать ли событие вырубоном для счётчика `⚡N`. Кнопка, штатное выключение,
    /// плановое обесточивание и сон — не считаются; неизвестное считается (лучше лишний
    /// вопрос, чем пропущенный дефект).</summary>
    public static bool CountsAsFailure(string? kind)
        => kind is not (PowerButton or Clean or Maintenance or PlannedOutage or Sleep);

    /// <summary>Человеческая подпись для CLI.</summary>
    public static string Describe(string? kind) => kind switch
    {
        HardOff => "обрыв питания",
        PowerButton => "кнопка питания",
        Bsod => "BSOD",
        Clean => "штатно",
        Maintenance => "обслуживание",
        PlannedOutage => "плановое обесточивание",
        Sleep => "сон",
        _ => "неизвестно",
    };
}
