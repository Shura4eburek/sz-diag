using SzDiag.Contracts;

namespace SzDiag.HubClient;

public enum SzLivenessState { Online, LagSuspected, NoContact, RevertFailed }

/// <summary>Одна классификация живости СЗ на CLI (`list`/`watch`) и Desk — иначе окно и
/// терминал начали бы расходиться в том, что считать «лагом», а что «нет связи».
///
/// Уверенного «вырубон» здесь нет сознательно: единственное надёжное подтверждение реального
/// отказа — смена boot-time при реконнекте (<see cref="SessionInfo.RebootCount"/>), а молчание
/// heartbeat само по себе им не является — под многочасовым OCCT 10 минут молчания штатны
/// (бэклог п.42, CLAUDE.md).</summary>
public static class SzLiveness
{
    /// <summary>Порог, после которого молчание уже не спишешь на лаг heartbeat под нагрузкой.</summary>
    public static readonly TimeSpan LikelyFailureThreshold = TimeSpan.FromMinutes(10);

    public static SzLivenessState Classify(SessionInfo s, DateTimeOffset now)
    {
        // Неудачный откат важнее всего остального: доступ мог остаться на клиенте навсегда
        // (бэклог п.59, СЗ 160705).
        if (!string.IsNullOrEmpty(s.RevertNote)) return SzLivenessState.RevertFailed;
        if (s.Status == SessionStatus.Online) return SzLivenessState.Online;
        return now - s.LastHeartbeat >= LikelyFailureThreshold
            ? SzLivenessState.NoContact
            : SzLivenessState.LagSuspected;
    }
}
