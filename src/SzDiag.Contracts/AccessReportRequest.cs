namespace SzDiag.Contracts;

/// <summary>Агент → hub: чем сейчас доступна машина. Отдельный метод, а не поле heartbeat,
/// потому что SignalR не поддерживает перегрузки hub-методов, а ломать сигнатуру
/// <c>Heartbeat(string)</c> нельзя — агенты старых сборок зовут её как есть.
///
/// Шлётся при открытии доступа и заново после ребута: quick tunnel НЕ сохраняет hostname
/// между запусками, поэтому имя обязано обновляться в течение сессии.</summary>
/// <param name="AccessHost">Имя, по которому хост подключается: hostname quick tunnel'а либо
/// null, если туннеля нет (тогда адресом остаётся IP).</param>
/// <param name="AccessMode">См. <see cref="SzDiag.Contracts.AccessMode"/>.</param>
/// <param name="SshHostKeyFingerprint">Публичный host-ключ нашего portable sshd. Якорь
/// доверия — этот канал (аутентифицирован, по TLS); по нему хост пинит ключ и перестаёт
/// ходить со StrictHostKeyChecking=no.</param>
public sealed record AccessReportRequest(string Sz, string? AccessHost = null,
    string? AccessMode = null, string? SshHostKeyFingerprint = null);
