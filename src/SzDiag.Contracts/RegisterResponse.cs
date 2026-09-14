namespace SzDiag.Contracts;

/// <summary>Ответ hub на регистрацию агента.</summary>
/// <param name="SessionSecret">Секрет этой сессии. Агент кладёт его в state.json и
/// предъявляет при headless-откате (watchdog, после ребута), где живого SignalR уже нет.
/// Общего токена там не хватает: он один на весь флот, а за Cloudflare Tunnel у всех
/// агентов ещё и одинаковый IP — сверка по адресу перестаёт различать машины.</param>
public sealed record RegisterResponse(string? SessionSecret = null);
