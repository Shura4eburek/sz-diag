using System.Globalization;

namespace SzDiag.Agent;

/// <summary>Отметка «агент жив» рядом с `state.json` — чтобы watchdog не срезал доступ под
/// работающей сессией.
///
/// Боль (бэклог п.85/п.81, СЗ 160306): watchdog-задача ставится один раз на `WatchdogHours` и
/// про агента не знает ничего. В 13:57 она снесла sshd, `svc-diag`, правило фаервола и
/// `state.json` — при живой сессии, идущих `exec`/`pull` и heartbeat. В CLI при этом
/// по-прежнему висело `online · готов`: запись об откате ушла в `revert.log`, и узнать правду
/// можно было только руками в планировщике.
///
/// Решение: агент касается файла-метки при каждом успешном heartbeat, а `--revert` перед
/// откатом смотрит на её свежесть. Свежая — доступ не трогаем и перевзводим задачу; молчит
/// дольше порога — откатываем. Потолок остаётся: даже при живом агенте доступ не должен
/// висеть вечно.</summary>
public static class AccessLiveness
{
    /// <summary>Насколько старой должна стать метка, чтобы считать агента мёртвым.
    /// 10 минут — с запасом больше любого разумного heartbeat (по умолчанию 30 с) и больше
    /// пауз, которые агент берёт под 100 % нагрузкой.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>Потолок на удержание доступа при живом агенте. Многочасовой прогон должен
    /// доживать до конца, но сутки без человека — уже след на клиентской машине.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromHours(24);

    /// <summary>Файл-метка рядом с state.json.</summary>
    public static string PathFor(string statePath) => statePath + ".alive";

    /// <summary>Отметиться живым. Ошибки глотаем: не смогли записать метку — это не повод
    /// ронять heartbeat-цикл, watchdog просто откатит по таймауту, как раньше.</summary>
    public static void Touch(string statePath)
    {
        try
        {
            File.WriteAllText(PathFor(statePath),
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch { /* метка — подсказка, а не критичный путь */ }
    }

    /// <summary>Когда агент отмечался в последний раз. null — метки нет (старая сборка,
    /// агент не стартовал, файл снесли).</summary>
    public static DateTimeOffset? LastSeen(string statePath)
    {
        try
        {
            var path = PathFor(statePath);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at) ? at : null;
        }
        catch { return null; }
    }

    public static void Delete(string statePath)
    {
        try { if (File.Exists(PathFor(statePath))) File.Delete(PathFor(statePath)); }
        catch { /* уборка не критична */ }
    }

    /// <summary>Решение watchdog: откатывать сейчас или отложить.</summary>
    /// <param name="lastSeen">Метка живости агента.</param>
    /// <param name="openedAt">Когда доступ был открыт (потолок удержания).</param>
    /// <param name="now">Текущее время.</param>
    public static bool ShouldRevert(DateTimeOffset? lastSeen, DateTimeOffset? openedAt, DateTimeOffset now)
    {
        // Метки нет — ведём себя как раньше: откатываем. Иначе агент старой сборки удерживал
        // бы доступ вечно.
        if (lastSeen is not { } seen) return true;

        // Потолок сильнее живости: сутки — это уже забытая сессия, а не длинный прогон.
        if (openedAt is { } opened && now - opened >= MaxHold) return true;

        return now - seen >= StaleAfter;
    }

    /// <summary>Почему watchdog решил именно так — строка в `revert.log`.</summary>
    public static string Explain(DateTimeOffset? lastSeen, DateTimeOffset? openedAt, DateTimeOffset now)
    {
        if (lastSeen is not { } seen) return "метки живости агента нет — откатываю";
        if (openedAt is { } opened && now - opened >= MaxHold)
            return $"доступ держится {(now - opened).TotalHours:N1} ч (потолок {MaxHold.TotalHours:N0} ч) — откатываю, " +
                   "даже несмотря на живого агента";
        var age = now - seen;
        return age >= StaleAfter
            ? $"агент молчит {age.TotalMinutes:N0} мин (порог {StaleAfter.TotalMinutes:N0}) — откатываю"
            : $"агент жив (heartbeat {age.TotalMinutes:N1} мин назад) — доступ НЕ трогаю, перевзвожу watchdog";
    }
}
