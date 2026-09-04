namespace SzDiag.Contracts;

/// <summary>Тело ответа `/sessions/{sz}/agent/restart` при HTTP 200 — различает «перезапуск
/// поставлен» от «СЗ не найдена среди активных» БЕЗ кода 404 (review W2 I-9): раньше оба
/// исхода (сессия не найдена / такого маршрута на hub нет вовсе) давали клиенту один и тот же
/// 404, и CLI против старого hub рапортовал «СЗ не найдена» вместо честного «hub старее CLI».
/// Тот же приём, что уже стоит в <c>AddNoteAsync</c>/<c>NoteResult</c>.</summary>
public sealed record RestartAgentResponse(bool Sent);
