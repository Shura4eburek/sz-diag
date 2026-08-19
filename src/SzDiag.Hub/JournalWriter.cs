using Microsoft.Extensions.Logging;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Пишет журнал СЗ от имени hub. Единственный писатель: CLI не трогает файл сам,
/// иначе две стороны дерутся за один markdown. Любая ошибка записи глотается — журнал не
/// должен становиться единой точкой отказа диагностики (упавший `Append` не имеет права
/// сорвать прогон или закрытие СЗ).</summary>
public sealed class JournalWriter
{
    private readonly ISzJournal _journal;
    private readonly IKnowledgeBaseScaffolder _kb;
    private readonly ILogger<JournalWriter> _log;
    private readonly Func<DateTimeOffset> _now;

    public JournalWriter(ISzJournal journal, IKnowledgeBaseScaffolder kb,
        ILogger<JournalWriter> log, Func<DateTimeOffset>? now = null)
    {
        _journal = journal;
        _kb = kb;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Действие, пришедшее командой `szcli`.</summary>
    public void Command(string sz, string text) => Write(sz, JournalSource.Command, text);

    /// <summary>Ручной шаг у машины (`szcli note`): свап железа, BIOS, осмотр.</summary>
    public void Manual(string sz, string text) => Write(sz, JournalSource.Manual, text);

    /// <summary>Событие клиента: вырубон, online/offline, остаток доступа после неполного отката.</summary>
    public void Machine(string sz, string text) => Write(sz, JournalSource.Machine, text);

    /// <summary>Дифф снимка конфигурации железа между прогонами.</summary>
    public void Snapshot(string sz, string text) => Write(sz, JournalSource.Snapshot, text);

    private void Write(string sz, JournalSource source, string text)
    {
        try
        {
            _kb.EnsureSkeleton(sz);
            _journal.Append(sz, new JournalEntry(_now(), source, text));
        }
        catch (Exception ex)
        {
            // Текст пишем в лог целиком: по нему запись восстанавливается руками.
            _log.LogWarning(ex, "СЗ {Sz}: не удалось записать в журнал: {Text}", sz, text);
        }
    }
}
