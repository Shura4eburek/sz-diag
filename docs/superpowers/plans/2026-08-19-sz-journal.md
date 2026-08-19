# Журнал СЗ (фазы 1–2) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Каждое действие по СЗ — команда `szcli`, ручной шаг мастера, событие машины — оседает строкой в `kb/СЗ/<номер>/журнал.md` по ходу процесса, а прогон без метки конфигурации становится невозможен.

**Architecture:** Hub — единственный писатель журнала. `SzJournal` (в `SzDiag.Kb`) знает формат файла и умеет дозаписывать/читать хвост; `JournalWriter` (в `SzDiag.Hub`) — фасад над ним, который глотает свои ошибки, чтобы падение записи никогда не валило команду. Команды CLI приходят через существующий `ManagementApi` и пишут журнал там же, где выполняются; события машины (вырубон, offline/online) пишутся из `AgentHub`/`OfflineSweeper`. Ручные заметки идут новым endpoint'ом `POST /api/sessions/{sz}/journal`, который намеренно **не требует активной сессии**.

**Tech Stack:** .NET 8 (net8.0), ASP.NET Core minimal API, SignalR, xUnit, Spectre.Console (CLI), SQLite (`Microsoft.Data.Sqlite`).

**Spec:** [docs/superpowers/specs/2026-08-19-sz-journal-design.md](../specs/2026-08-19-sz-journal-design.md)

**Scope этого плана:** фазы 1 и 2 из спеки (журнал + `szcli note` + обязательная метка конфигурации). Фазы 3 и 4 (автоснапшот железа с диффом, `szcli todo` / stale-детект / гейт в `close`) — отдельный план после того, как этот заедет: они опираются на `SzJournal.LastEntryAt`, который здесь появляется, и их проще писать по факту работающего журнала.

## Global Constraints

- Целевой фреймворк — **net8.0**, все новые файлы в существующих проектах `src/SzDiag.Kb`, `src/SzDiag.Hub`, `src/SzDiag.Cli`, `src/SzDiag.Contracts`.
- **Контент журнала — украинский** (kb ведётся на украинском: сервис и колл-центр украиноязычные). Комментарии в коде и вывод CLI — **русский**, как в остальном коде.
- Имена файлов kb задаются **только** в `KbPaths` — строк `"журнал.md"` в других файлах быть не должно.
- Файлы `.cs` — UTF-8 **с BOM** (PowerShell 5.1 иначе ломает кириллицу, коммит `3e60857`); в репозитории так у всех существующих файлов.
- Запись в журнал **никогда** не валит команду: любое исключение внутри `JournalWriter` логируется и глотается.
- Номер СЗ валидируется через существующий `SzNumber` (ровно 6 цифр) — мусорный ввод не должен заводить папки в kb (бэклог п.57).
- Тесты пишутся в зеркальные проекты `tests/SzDiag.Kb.Tests`, `tests/SzDiag.Hub.Tests`, `tests/SzDiag.Cli.Tests`; временные vault'ы — во временной папке (`Path.GetTempPath()`), как в `KbRecorderTests`.
- Полный прогон: `dotnet test` (~481 тест) должен оставаться зелёным после каждой задачи.

---

### Task 1: `SzJournal` — формат журнала и дозапись

**Files:**
- Modify: `src/SzDiag.Kb/KbPaths.cs`
- Create: `src/SzDiag.Kb/SzJournal.cs`
- Test: `tests/SzDiag.Kb.Tests/SzJournalTests.cs`
- Test (modify): `tests/SzDiag.Kb.Tests/KbPathsTests.cs`

**Interfaces:**
- Consumes: `KbPaths` (существующий, `SzDir(sz)`).
- Produces:
  - `KbPaths.Journal(string sz) : string`, `KbPaths.SnapshotsDir(string sz) : string`
  - `enum JournalSource { Command, Manual, Machine, Snapshot }`
  - `record JournalEntry(DateTimeOffset At, JournalSource Source, string Text)`
  - `interface ISzJournal { void Append(string sz, JournalEntry entry); DateTimeOffset? LastEntryAt(string sz); IReadOnlyList<JournalEntry> Tail(string sz, int count); }`
  - `sealed class SzJournal : ISzJournal` с конструктором `SzJournal(KbPaths paths)`

- [ ] **Step 1: Написать падающий тест на формат первой записи**

Создать `tests/SzDiag.Kb.Tests/SzJournalTests.cs`:

```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class SzJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szjrn-{Guid.NewGuid():N}");
    private readonly KbPaths _paths;

    public SzJournalTests() => _paths = new KbPaths(_root);

    private static DateTimeOffset At(int day, int hour, int min) =>
        new(2026, 8, day, hour, min, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Append_FirstEntry_WritesTitleDayHeaderAndLine()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command,
            "`test run occt` — старт"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Contains("# Журнал 160697", text);
        Assert.Contains("## 2026-08-10", text);
        Assert.Contains("- **17:04** `test run occt` — старт", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Прогнать тест, убедиться что падает**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~SzJournalTests`
Expected: FAIL — компиляция не проходит, `SzJournal` и `KbPaths.Journal` не существуют.

- [ ] **Step 3: Добавить пути в `KbPaths`**

В `src/SzDiag.Kb/KbPaths.cs`, рядом с `Summary`:

```csharp
    /// <summary>Журнал СЗ: сырьё процесса — команды, ручные шаги мастера, события машины.
    /// Пишется автоматически по ходу диагностики; `дії.md` остаётся человеческим пересказом.</summary>
    public string Journal(string sz) => Path.Combine(SzDir(sz), "журнал.md");

    /// <summary>Снимки конфигурации железа (JSON) — для диффа «что изменилось между прогонами».</summary>
    public string SnapshotsDir(string sz) => Path.Combine(SzDir(sz), "snapshots");
```

- [ ] **Step 4: Написать минимальную реализацию `SzJournal`**

Создать `src/SzDiag.Kb/SzJournal.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace SzDiag.Kb;

/// <summary>Откуда взялась запись журнала. Значок в файле различает их визуально:
/// команда — без значка, рука мастера — ✋, событие машины — ⚡, дифф снимка железа — 🔧.</summary>
public enum JournalSource { Command, Manual, Machine, Snapshot }

/// <param name="At">Момент события (локальное время сервисного бокса).</param>
public sealed record JournalEntry(DateTimeOffset At, JournalSource Source, string Text);

public interface ISzJournal
{
    void Append(string sz, JournalEntry entry);
    DateTimeOffset? LastEntryAt(string sz);
    IReadOnlyList<JournalEntry> Tail(string sz, int count);
}

/// <summary>Журнал СЗ: единственное место, знающее формат `журнал.md`. Дозапись только
/// в конец файла — журнал не переписывается, чтобы уже записанное нельзя было потерять
/// при сбое посреди операции.</summary>
public sealed class SzJournal : ISzJournal
{
    private const string DayFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH\\:mm";
    private readonly KbPaths _paths;
    private readonly object _lock = new();

    public SzJournal(KbPaths paths) => _paths = paths;

    public void Append(string sz, JournalEntry entry)
    {
        var path = _paths.Journal(sz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        lock (_lock)
        {
            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            var sb = new StringBuilder(existing);

            if (existing.Length == 0)
                sb.Append($"# Журнал {sz}{Environment.NewLine}{Environment.NewLine}");

            var day = entry.At.ToString(DayFormat, CultureInfo.InvariantCulture);
            if (!existing.Contains($"## {day}", StringComparison.Ordinal))
                sb.Append($"## {day}{Environment.NewLine}");

            sb.Append(Line(entry)).Append(Environment.NewLine);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }

    private static string Line(JournalEntry entry)
    {
        var mark = entry.Source switch
        {
            JournalSource.Manual => "✋ ",
            JournalSource.Machine => "⚡ ",
            JournalSource.Snapshot => "🔧 ",
            _ => "",
        };
        var time = entry.At.ToString(TimeFormat, CultureInfo.InvariantCulture);
        return $"- **{time}** {mark}{entry.Text}";
    }

    public DateTimeOffset? LastEntryAt(string sz) => throw new NotImplementedException();

    public IReadOnlyList<JournalEntry> Tail(string sz, int count) => throw new NotImplementedException();
}
```

- [ ] **Step 5: Прогнать тест, убедиться что проходит**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~SzJournalTests`
Expected: PASS.

- [ ] **Step 6: Написать падающие тесты на заголовки дней и значки источников**

Дописать в `SzJournalTests`:

```csharp
    [Fact]
    public void Append_SecondEntrySameDay_DoesNotDuplicateDayHeader()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(10, 17, 21), JournalSource.Machine, "друга"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Single(text.Split("## 2026-08-10")[1..]);
        Assert.Contains("- **17:21** ⚡ друга", text);
    }

    [Fact]
    public void Append_NextDay_AddsNewDayHeader()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(11, 9, 30), JournalSource.Manual, "друга"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Contains("## 2026-08-10", text);
        Assert.Contains("## 2026-08-11", text);
        Assert.Contains("- **09:30** ✋ друга", text);
    }

    [Fact]
    public void Append_CyrillicAndMarkdown_SurviveRoundTrip()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 38), JournalSource.Manual,
            "майстер зняв **Gigabyte UD850GM**, поставив тестовий Corsair RM850x"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Contains("майстер зняв **Gigabyte UD850GM**, поставив тестовий Corsair RM850x", text);
    }
```

- [ ] **Step 7: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~SzJournalTests`
Expected: PASS (реализация из шага 4 их покрывает; если `Assert.Single` падает — заголовок дня задваивается, чинить условие `existing.Contains`).

- [ ] **Step 8: Написать падающие тесты на `LastEntryAt` и `Tail`**

```csharp
    [Fact]
    public void LastEntryAt_NoFile_ReturnsNull()
    {
        Assert.Null(new SzJournal(_paths).LastEntryAt("160697"));
    }

    [Fact]
    public void LastEntryAt_ReturnsMomentOfLastLine()
    {
        var journal = new SzJournal(_paths);
        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(11, 9, 30), JournalSource.Manual, "друга"));

        var last = journal.LastEntryAt("160697");

        Assert.Equal(new DateTime(2026, 8, 11, 9, 30, 0), last!.Value.DateTime);
    }

    [Fact]
    public void Tail_ReturnsLastEntriesInOrder_EvenIfFewerThanRequested()
    {
        var journal = new SzJournal(_paths);
        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(10, 17, 21), JournalSource.Machine, "друга"));

        var tail = journal.Tail("160697", 5);

        Assert.Equal(2, tail.Count);
        Assert.Equal("перша", tail[0].Text);
        Assert.Equal(JournalSource.Machine, tail[1].Source);
    }
```

- [ ] **Step 9: Прогнать, убедиться что падают**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~SzJournalTests`
Expected: FAIL — `NotImplementedException` в `LastEntryAt`/`Tail`.

- [ ] **Step 10: Реализовать чтение журнала**

Заменить заглушки в `src/SzDiag.Kb/SzJournal.cs`:

```csharp
    public DateTimeOffset? LastEntryAt(string sz)
    {
        var entries = ReadAll(sz);
        return entries.Count == 0 ? null : entries[^1].At;
    }

    public IReadOnlyList<JournalEntry> Tail(string sz, int count)
    {
        var entries = ReadAll(sz);
        return count >= entries.Count ? entries : entries[^count..];
    }

    /// <summary>Разбор файла обратно в записи: дата берётся из заголовка дня, время —
    /// из начала строки. Строки, не похожие на запись, пропускаются молча (в файл могли
    /// дописать руками — это не повод падать).</summary>
    private List<JournalEntry> ReadAll(string sz)
    {
        var result = new List<JournalEntry>();
        var path = _paths.Journal(sz);
        if (!File.Exists(path)) return result;

        DateTime? day = null;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)
                && DateTime.TryParseExact(line[3..].Trim(), DayFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedDay))
            {
                day = parsedDay;
                continue;
            }

            if (day is null || !line.StartsWith("- **", StringComparison.Ordinal)) continue;
            var close = line.IndexOf("**", 4, StringComparison.Ordinal);
            if (close < 0) continue;
            if (!TimeSpan.TryParseExact(line[4..close], "hh\\:mm", CultureInfo.InvariantCulture,
                    out var time)) continue;

            var rest = line[(close + 2)..].TrimStart();
            var source = JournalSource.Command;
            foreach (var (mark, kind) in Marks)
            {
                if (!rest.StartsWith(mark, StringComparison.Ordinal)) continue;
                source = kind;
                rest = rest[mark.Length..].TrimStart();
                break;
            }

            result.Add(new JournalEntry(new DateTimeOffset(day.Value + time, TimeSpan.Zero),
                source, rest));
        }

        return result;
    }

    private static readonly (string Mark, JournalSource Kind)[] Marks =
    {
        ("✋", JournalSource.Manual),
        ("⚡", JournalSource.Machine),
        ("🔧", JournalSource.Snapshot),
    };
```

- [ ] **Step 11: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~SzJournalTests`
Expected: PASS (все 7 тестов).

- [ ] **Step 12: Добавить тест путей в `KbPathsTests`**

```csharp
    [Fact]
    public void Journal_And_Snapshots_LiveInsideSzFolder()
    {
        var paths = new KbPaths(@"C:\kb");

        Assert.Equal(@"C:\kb\СЗ\160697\журнал.md", paths.Journal("160697"));
        Assert.Equal(@"C:\kb\СЗ\160697\snapshots", paths.SnapshotsDir("160697"));
    }
```

- [ ] **Step 13: Прогнать весь проект тестов Kb**

Run: `dotnet test tests/SzDiag.Kb.Tests`
Expected: PASS.

- [ ] **Step 14: Коммит**

```bash
git add src/SzDiag.Kb/KbPaths.cs src/SzDiag.Kb/SzJournal.cs tests/SzDiag.Kb.Tests/SzJournalTests.cs tests/SzDiag.Kb.Tests/KbPathsTests.cs
git commit -m "feat(kb): SzJournal — формат журнала СЗ, дозапись и чтение хвоста"
```

---

### Task 2: `JournalWriter` — фасад в hub, который не валит команды

**Files:**
- Create: `src/SzDiag.Hub/JournalWriter.cs`
- Modify: `src/SzDiag.Hub/Program.cs` (регистрация в DI, рядом с `AddSingleton<IKnowledgeBaseScaffolder>`)
- Test: `tests/SzDiag.Hub.Tests/JournalWriterTests.cs`

**Interfaces:**
- Consumes: `ISzJournal`, `JournalEntry`, `JournalSource` (Task 1); существующие `IKnowledgeBaseScaffolder.EnsureSkeleton(string sz)`, `KbPaths`.
- Produces: `sealed class JournalWriter` с методами `Command(string sz, string text)`, `Manual(string sz, string text)`, `Machine(string sz, string text)`, `Snapshot(string sz, string text)` — все `void`, ни один не бросает.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/JournalWriterTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class JournalWriterTests
{
    private sealed class FakeJournal : ISzJournal
    {
        public List<(string Sz, JournalEntry Entry)> Written { get; } = new();
        public Exception? Throw { get; set; }

        public void Append(string sz, JournalEntry entry)
        {
            if (Throw is not null) throw Throw;
            Written.Add((sz, entry));
        }

        public DateTimeOffset? LastEntryAt(string sz) => null;
        public IReadOnlyList<JournalEntry> Tail(string sz, int count) => Array.Empty<JournalEntry>();
    }

    private sealed class FakeScaffolder : IKnowledgeBaseScaffolder
    {
        public List<string> Ensured { get; } = new();
        public void EnsureSkeleton(string sz) => Ensured.Add(sz);
    }

    [Fact]
    public void Manual_WritesEntryWithManualSource_AndEnsuresSkeleton()
    {
        var journal = new FakeJournal();
        var scaffolder = new FakeScaffolder();
        var writer = new JournalWriter(journal, scaffolder, NullLogger<JournalWriter>.Instance);

        writer.Manual("160697", "поставив тестовий Corsair RM850x");

        var (sz, entry) = Assert.Single(journal.Written);
        Assert.Equal("160697", sz);
        Assert.Equal(JournalSource.Manual, entry.Source);
        Assert.Equal("поставив тестовий Corsair RM850x", entry.Text);
        Assert.Equal("160697", Assert.Single(scaffolder.Ensured));
    }

    [Fact]
    public void Append_WhenJournalThrows_DoesNotPropagate()
    {
        var journal = new FakeJournal { Throw = new IOException("vault занят") };
        var writer = new JournalWriter(journal, new FakeScaffolder(), NullLogger<JournalWriter>.Instance);

        var ex = Record.Exception(() => writer.Command("160697", "`push occt` — принято"));

        Assert.Null(ex);
    }
}
```

**Важно:** если сигнатура `IKnowledgeBaseScaffolder` в репозитории отличается от `void EnsureSkeleton(string sz)` — привести `FakeScaffolder` в соответствие с реальным интерфейсом (`src/SzDiag.Kb/IKnowledgeBaseScaffolder.cs`), реализовав все его члены.

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~JournalWriterTests`
Expected: FAIL — `JournalWriter` не существует.

- [ ] **Step 3: Написать реализацию**

Создать `src/SzDiag.Hub/JournalWriter.cs`:

```csharp
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
```

- [ ] **Step 4: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~JournalWriterTests`
Expected: PASS.

- [ ] **Step 5: Зарегистрировать в DI**

В `src/SzDiag.Hub/Program.cs`, после регистрации `IKnowledgeBaseScaffolder`:

```csharp
builder.Services.AddSingleton<ISzJournal>(sp =>
    new SzJournal(new KbPaths(sp.GetRequiredService<IOptions<HubOptions>>().Value.KnowledgeBaseRoot)));
builder.Services.AddSingleton<JournalWriter>();
```

**Важно:** `KnowledgeBaseRoot` резолвится так же, как в существующей регистрации `IKnowledgeBaseScaffolder` (там путь уже приводится к абсолютному от `AppContext.BaseDirectory`) — повторить ровно тот же способ, а не изобретать свой.

- [ ] **Step 6: Собрать солюшен и прогнать все тесты**

Run: `dotnet build; dotnet test`
Expected: сборка без ошибок, тесты зелёные.

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Hub/JournalWriter.cs src/SzDiag.Hub/Program.cs tests/SzDiag.Hub.Tests/JournalWriterTests.cs
git commit -m "feat(hub): JournalWriter — запись журнала СЗ, ошибки не валят команду"
```

---

### Task 3: Endpoint `POST /api/sessions/{sz}/journal`

**Files:**
- Create: `src/SzDiag.Contracts/JournalNoteRequest.cs`
- Modify: `src/SzDiag.Hub/ManagementApi.cs`
- Test: `tests/SzDiag.Hub.Tests/ManagementApiJournalTests.cs`

**Interfaces:**
- Consumes: `JournalWriter` (Task 2), существующий `SzNumber` из `SzDiag.Contracts`.
- Produces: `record JournalNoteRequest(string Text)`; endpoint `POST /api/sessions/{sz}/journal` → `200 OK` / `400 BadRequest`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/ManagementApiJournalTests.cs`. Фикстура — ровно та же, что в существующем `tests/SzDiag.Hub.Tests/ManagementApiTests.cs`: `WebApplicationFactory<Program>` с `UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)` и `.WithoutSystemLogging()`. Результат проверяем **по файлу журнала в тестовом vault**, а не по моку — так тест заодно ловит формат.

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ManagementApiJournalTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-jrn-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-jrn-{Guid.NewGuid():N}");

    public ManagementApiJournalTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");
        return client;
    }

    private string JournalText(string sz) =>
        File.ReadAllText(Path.Combine(_kbRoot, "СЗ", sz, "журнал.md"));

    private bool JournalExists(string sz) =>
        File.Exists(Path.Combine(_kbRoot, "СЗ", sz, "журнал.md"));

    [Fact]
    public async Task Journal_ValidNote_ReturnsOk_AndWritesManualEntry()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160697/journal",
            new JournalNoteRequest("поставив тестовий Corsair RM850x"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("✋ поставив тестовий Corsair RM850x", JournalText("160697"));
    }

    [Fact]
    public async Task Journal_EmptyText_ReturnsBadRequest()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160698/journal",
            new JournalNoteRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(JournalExists("160698"));
    }

    [Fact]
    public async Task Journal_BadSzNumber_ReturnsBadRequest_AndCreatesNothing()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/--help/journal",
            new JournalNoteRequest("текст"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_kbRoot, "СЗ", "--help")));
    }

    [Fact]
    public async Task Journal_NoActiveSession_StillAccepted()
    {
        // Ни одного агента не регистрировали: заметка мастера должна приниматься всё равно.
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160699/journal",
            new JournalNoteRequest("майстер вимкнув EXPO в BIOS"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("майстер вимкнув EXPO в BIOS", JournalText("160699"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~ManagementApiJournalTests`
Expected: FAIL — `JournalNoteRequest` не существует, маршрут отдаёт 404.

- [ ] **Step 3: Добавить DTO**

Создать `src/SzDiag.Contracts/JournalNoteRequest.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>Ручная запись в журнал СЗ (`szcli note`): то, что софтом не видно —
/// свап БП, правка BIOS, осмотр мастером.</summary>
public sealed record JournalNoteRequest(string Text);
```

- [ ] **Step 4: Добавить endpoint**

В `src/SzDiag.Hub/ManagementApi.cs`, рядом с остальными `sessions/{sz}/…`:

```csharp
        // Заметку принимаем даже когда сессии нет: мастер отходит от машины, агент может быть
        // уже offline или СЗ закрыта, а зафиксировать физический шаг надо в момент, когда он
        // сделан — иначе информация теряется вместе с сессией (СЗ 160697).
        group.MapPost("/sessions/{sz}/journal", (string sz, JournalNoteRequest body,
            JournalWriter journal) =>
        {
            if (!SzNumber.IsValid(sz)) return Results.BadRequest("номер СЗ — ровно 6 цифр");
            if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest("пустая заметка");
            journal.Manual(sz, body.Text.Trim());
            return Results.Ok();
        });
```

**Важно:** проверить фактическое имя метода валидации в `SzDiag.Contracts/SzNumber.cs` (`IsValid` / `TryParse` / `Validate`) и использовать существующий, не добавляя новый.

- [ ] **Step 5: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~ManagementApiJournalTests`
Expected: PASS (4 теста).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Contracts/JournalNoteRequest.cs src/SzDiag.Hub/ManagementApi.cs tests/SzDiag.Hub.Tests/ManagementApiJournalTests.cs
git commit -m "feat(hub): endpoint журнала СЗ — ручные заметки принимаются и без активной сессии"
```

---

### Task 4: Команда `szcli note`

**Files:**
- Modify: `src/SzDiag.Cli/IHubApiClient.cs`
- Modify: `src/SzDiag.Cli/HubApiClient.cs`
- Modify: `src/SzDiag.Cli/Program.cs`
- Test (modify): `tests/SzDiag.Cli.Tests/HubApiClientTests.cs`

**Interfaces:**
- Consumes: `JournalNoteRequest` (Task 3), endpoint из Task 3.
- Produces: `IHubApiClient.AddNoteAsync(string sz, string text, CancellationToken ct = default) : Task<bool>`; команда `szcli note <СЗ> "<текст>"`.

- [ ] **Step 1: Написать падающий тест на разбор аргументов**

`Program.cs` в CLI — top-level statements, тестов на разбор аргументов в проекте нет; команды покрываются на уровне `HubApiClient` через `StubHandler` (см. `tests/SzDiag.Cli.Tests/HubApiClientTests.cs`). Идём тем же путём — дописать в `HubApiClientTests`:

```csharp
    [Fact]
    public async Task AddNoteAsync_PostsTextToJournalEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        var ok = await client.AddNoteAsync("160697", "поставив тестовий Corsair RM850x");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/sessions/160697/journal", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("поставив тестовий Corsair RM850x",
            await handler.LastRequest.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddNoteAsync_WhenHubRejects_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.BadRequest));

        Assert.False(await client.AddNoteAsync("160697", "текст"));
    }
```

Разбор самих аргументов проверяется вручную на собранном dist (Task 10, шаг 4) — так же, как для остальных существующих команд CLI.

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~AddNoteAsync`
Expected: FAIL — компиляция не проходит, метода `AddNoteAsync` нет.

- [ ] **Step 3: Добавить метод в интерфейс и клиент**

В `src/SzDiag.Cli/IHubApiClient.cs`:

```csharp
    Task<bool> AddNoteAsync(string sz, string text, CancellationToken ct = default);
```

В `src/SzDiag.Cli/HubApiClient.cs` (по образцу соседних методов, с тем же заголовком токена):

```csharp
    public async Task<bool> AddNoteAsync(string sz, string text, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"/api/sessions/{sz}/journal",
            new JournalNoteRequest(text), ct);
        return res.IsSuccessStatusCode;
    }
```

- [ ] **Step 4: Добавить разбор команды**

В `src/SzDiag.Cli/Program.cs`, в `switch` рядом с `case "reboots"`:

```csharp
    // Ручной шаг у машины (свап железа, правка BIOS, осмотр). Пишется в журнал СЗ сразу,
    // потому что до конца дня оно забывается: на 160697 свап БП так и пропал.
    case "note" when args.Length >= 3:
    {
        var noteSz = args[1];
        if (!SzNumber.IsValid(noteSz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Номер СЗ — ровно 6 цифр:[/] {noteSz}");
            return CliErrors.BadArguments;
        }

        // Кавычки вокруг текста необязательны: всё, что после номера, — одна заметка.
        var noteText = string.Join(' ', args[2..]);
        if (await client.AddNoteAsync(noteSz, noteText))
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {noteSz}: записано в журнал[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {noteSz}: hub не принял заметку[/]");
        break;
    }
```

**Важно:** имя константы кода возврата взять фактическое из `src/SzDiag.Cli/CliErrors.cs`.

- [ ] **Step 5: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~HubApiClientTests`
Expected: PASS.

- [ ] **Step 6: Добавить команду в справку CLI**

Найти в `src/SzDiag.Cli/Program.cs` печать справки (блок с перечислением команд) и добавить строку в том же стиле:

```
  note <СЗ> <текст>          записать ручной шаг в журнал СЗ (свап железа, BIOS, осмотр)
```

- [ ] **Step 7: Прогнать все тесты и закоммитить**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/SzDiag.Cli tests/SzDiag.Cli.Tests/HubApiClientTests.cs
git commit -m "feat(cli): szcli note — ручной шаг мастера сразу в журнал СЗ"
```

---

### Task 5: Команды `ManagementApi` пишут журнал

**Files:**
- Modify: `src/SzDiag.Hub/ManagementApi.cs`
- Test: `tests/SzDiag.Hub.Tests/ManagementApiJournalTests.cs` (дописать)

**Interfaces:**
- Consumes: `JournalWriter.Command` (Task 2).
- Produces: записи журнала для `test`, `diag`, `exec`, `push`, `pull`, `close`, `maintenance`.

- [ ] **Step 1: Написать падающие тесты**

Дописать в `ManagementApiJournalTests` (та же фикстура, проверка по файлу журнала):

```csharp
    [Fact]
    public async Task Diag_WhenSessionMissing_WritesNothing()
    {
        // Сессии нет — команда не выполнилась, и врать про неё в журнале нельзя.
        var res = await NewClient().PostAsync("/api/sessions/160701/diag", null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(JournalExists("160701"));
    }

    [Fact]
    public async Task Close_WhenSessionMissing_WritesNothing()
    {
        var res = await NewClient().PostAsync("/api/sessions/160702/close", null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(JournalExists("160702"));
    }
```

Тест на **успешный** путь требует онлайн-сессии: взять способ её подъёма из существующего `tests/SzDiag.Hub.Tests/PushEndToEndTests.cs` (там агент подключается по SignalR к тестовому хосту) и после успешной команды проверить:

```csharp
        Assert.Contains("`push occt`", JournalText("160697"));
```

**Важно:** фактические сигнатуры `PushCommandRequest`/`ExecCommandRequest`/`MaintenanceWindow` взять из `src/SzDiag.Contracts` — параметры могут отличаться от показанных.

- [ ] **Step 2: Прогнать, убедиться что падают**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~ManagementApiJournalTests`
Expected: FAIL — записей нет.

- [ ] **Step 3: Дописать вызовы журнала в обработчики**

В `src/SzDiag.Hub/ManagementApi.cs` — **только по факту успеха**, чтобы журнал не врал про то, чего не произошло:

```csharp
        group.MapPost("/sessions/{sz}/close", async (string sz, SessionCloser closer,
            JournalWriter journal) =>
        {
            if (!await closer.CloseAsync(sz)) return Results.NotFound();
            journal.Command(sz, "`close` — доступ згорнуто, сесію закрито");
            return Results.Ok();
        });

        group.MapPost("/sessions/{sz}/diag", async (string sz, string? sections,
            DiagRunTrigger trigger, JournalWriter journal) =>
        {
            if (!await trigger.TriggerAsync(sz, sections)) return Results.NotFound();
            journal.Command(sz, $"`diag run` — старт (секції: {sections ?? "усі"})");
            return Results.Ok();
        });
```

Аналогично для `exec` (текст: ``$"`exec` — скрипт прийнято ({script.Length} символів){(body.Detached ? ", detached" : "")}"``), `push` (``$"`push {body.Tool}` — доставка інструмента"``), `pull` (``$"`pull {body.Path}` — забір файлів"``) и `maintenance` (``$"`maintenance` — вікно обслуговування {body.From:HH:mm}–{body.To:HH:mm}"``). Точные имена полей DTO — из `src/SzDiag.Contracts`.

`test` в этой задаче **не трогаем** — он переписывается в Task 7 вместе с меткой конфигурации.

- [ ] **Step 4: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~ManagementApiJournalTests`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hub/ManagementApi.cs tests/SzDiag.Hub.Tests/ManagementApiJournalTests.cs
git commit -m "feat(hub): команды CLI оседают в журнале СЗ"
```

---

### Task 6: События машины в журнале

**Files:**
- Modify: `src/SzDiag.Hub/AgentHub.cs` (метод `Register`)
- Modify: `src/SzDiag.Hub/OfflineSweeper.cs`
- Test: `tests/SzDiag.Hub.Tests/AgentHubJournalTests.cs`

**Interfaces:**
- Consumes: `JournalWriter.Machine` (Task 2), существующие `SessionRegistry.RegisterOutcome`, `ShutdownKind`, `SessionRegistry.MarkStaleOffline`.
- Produces: записи журнала о вырубоне и уходе в offline.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/AgentHubJournalTests.cs` **по образцу существующего `tests/SzDiag.Hub.Tests/RebootEndToEndTests.cs`** — там уже отработан сценарий «агент подключился с одним boot-time, потом с другим». Оттуда же берётся подъём тестового хоста с `Hub:KnowledgeBaseRoot` в temp-папке; проверяем файл журнала:

```csharp
    [Fact]
    public async Task Register_WhenBootTimeChanged_WritesMachineEntryWithUptime()
    {
        // Агент коннектится дважды: второй раз с новым boot-time — это и есть вырубон.
        await ConnectAgentAsync("160697", boot: new DateTimeOffset(2026, 8, 10, 17, 1, 0, TimeSpan.Zero),
            lastShutdown: ShutdownKind.PowerLoss);
        await ConnectAgentAsync("160697", boot: new DateTimeOffset(2026, 8, 10, 17, 22, 0, TimeSpan.Zero),
            lastShutdown: ShutdownKind.PowerLoss);

        var text = JournalText("160697");
        Assert.Contains("⚡", text);
        Assert.Contains("вирубон", text);
        Assert.Contains("00:21", text);              // продержалась 21 минуту
    }

    [Fact]
    public async Task Register_FirstConnect_WritesNoRebootEntry()
    {
        await ConnectAgentAsync("160704", boot: new DateTimeOffset(2026, 8, 10, 17, 1, 0, TimeSpan.Zero));

        Assert.DoesNotContain("вирубон", JournalExists("160704") ? JournalText("160704") : "");
    }
```

**Важно:** `ConnectAgentAsync` — обёртка теста над реальным подключением агента; собрать её ровно так, как это сделано в `RebootEndToEndTests`. Фактическое имя константы вида `ShutdownKind.PowerLoss` взять из `src/SzDiag.Contracts/ShutdownKind.cs` — важно, чтобы `CountsAsFailure` вернул `true`, иначе в журнале будет «перезавантаження», а не «вирубон».

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AgentHubJournalTests`
Expected: FAIL — записей нет (в `AgentHub` нет `JournalWriter`).

- [ ] **Step 3: Прокинуть `JournalWriter` в `AgentHub` и писать вырубон**

В `src/SzDiag.Hub/AgentHub.cs` добавить `JournalWriter` в конструктор (рядом с `_store`, `_kb`) и в блоке `if (outcome.Rebooted)` после `RecordRebootAsync`:

```csharp
            // Та же формулировка, что в консоли hub: журнал должен читаться без сверки с логом.
            _journal.Machine(request.Sz,
                $"**{(failure ? "вирубон" : "перезавантаження")}** — {ShutdownKind.Describe(request.LastShutdown)}" +
                $"{held}{busy}");
```

(`held`/`busy`/`failure` уже посчитаны выше по методу.)

- [ ] **Step 4: Писать уход в offline**

В `src/SzDiag.Hub/OfflineSweeper.cs`, там где `MarkStaleOffline` возвращает список СЗ, — по строке на каждую:

```csharp
        foreach (var sz in stale)
            _journal.Machine(sz, "зв'язок втрачено (heartbeat не приходить)");
```

**Важно:** `OfflineSweeper` — hosted service; `JournalWriter` берётся через конструктор из DI, как остальные зависимости этого класса. Проверить существующие `tests/SzDiag.Hub.Tests/OfflineSweeperTests.cs` — конструктор изменился, тесты надо поправить.

- [ ] **Step 4b: Писать остаток доступа при неполном откате**

В координаторе отката (класс `RevertCoordinator`, найти файл: `grep -rl "class RevertCoordinator" src/`) — там, где фиксируется неполный откат и watchdog перевзводится на +10 минут:

```csharp
        _journal.Machine(sz, "⚠️ відкат неповний — на машині лишився доступ, watchdog перевзведено");
```

Если `RevertCoordinator` живёт в проекте агента, а не hub (проверить по результату `grep`), — этот шаг переносится в фазу 3–4, где будет канал агент → hub для таких событий; тогда просто отметить шаг как неприменимый и записать причину в план следующей фазы.

- [ ] **Step 5: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests`
Expected: PASS.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hub/AgentHub.cs src/SzDiag.Hub/OfflineSweeper.cs tests/SzDiag.Hub.Tests/AgentHubJournalTests.cs
git commit -m "feat(hub): вырубоны и потеря связи пишутся в журнал СЗ"
```

---

### Task 7: Обязательная метка конфигурации у прогона (сторона hub)

**Files:**
- Modify: `src/SzDiag.Contracts/` — новый `TestRunRequest.cs`
- Modify: `src/SzDiag.Hub/ManagementApi.cs`
- Modify: `src/SzDiag.Hub/ISessionStore.cs`, `src/SzDiag.Hub/SqliteSessionStore.cs`
- Test: `tests/SzDiag.Hub.Tests/TestRunConfigTests.cs`

**Interfaces:**
- Consumes: `JournalWriter.Command` (Task 2), существующий `TestRunTrigger.TriggerAsync(string sz, string? filter)`.
- Produces:
  - `record TestRunRequest(string? Filter, string? Config, bool SameConfig)`
  - `ISessionStore.SetLastTestConfigAsync(string sz, string config, CancellationToken ct = default) : Task`
  - `ISessionStore.GetLastTestConfigAsync(string sz, CancellationToken ct = default) : Task<string?>`
  - `POST /api/sessions/{sz}/test` принимает тело `TestRunRequest`; `400` без метки.

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/SzDiag.Hub.Tests/TestRunConfigTests.cs` с той же фикстурой, что в `ManagementApiJournalTests` (Task 3). Гейт срабатывает **до** обращения к сессии, поэтому 400-кейсы онлайн-агента не требуют:

```csharp
    [Fact]
    public async Task Test_WithoutConfig_ReturnsBadRequest_AndWritesNothing()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160697/test",
            new TestRunRequest("occt", null, false));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("--config", await res.Content.ReadAsStringAsync());
        Assert.False(JournalExists("160697"));
    }

    [Fact]
    public async Task Test_SameConfig_WithoutStoredLabel_ReturnsBadRequest()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160697/test",
            new TestRunRequest("occt", null, SameConfig: true));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Test_WithoutConfig_AfterStoredLabel_HintsSameConfig()
    {
        var store = _factory.Services.GetRequiredService<ISessionStore>();
        await store.SetLastTestConfigAsync("160697", "EXPO 6000, штатний БЖ");

        var res = await NewClient().PostAsJsonAsync("/api/sessions/160697/test",
            new TestRunRequest("occt", null, false));

        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("EXPO 6000, штатний БЖ", body);
        Assert.Contains("--same-config", body);
    }

    [Fact]
    public async Task Test_WithConfig_ButNoSession_ReturnsNotFound_AndDoesNotRememberLabel()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160703/test",
            new TestRunRequest("occt", "сток JEDEC 4800", false));

        var store = _factory.Services.GetRequiredService<ISessionStore>();
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Null(await store.GetLastTestConfigAsync("160703"));
    }
```

Успешный путь (прогон реально стартовал, метка запомнилась и попала в журнал) требует онлайн-агента — дописать по образцу `tests/SzDiag.Hub.Tests/PushEndToEndTests.cs`:

```csharp
        Assert.Equal("EXPO 6000, штатний БЖ", await store.GetLastTestConfigAsync("160697"));
        Assert.Contains("конфігурація: **EXPO 6000, штатний БЖ**", JournalText("160697"));
```

Отдельно — юнит-тест стора в существующем `tests/SzDiag.Hub.Tests/SqliteSessionStoreTests.cs`:

```csharp
    [Fact]
    public async Task TestConfig_LastValueWins()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.SetLastTestConfigAsync("160697", "EXPO 6000, штатний БЖ");
        await store.SetLastTestConfigAsync("160697", "сток 4800, тестовий БЖ");

        Assert.Equal("сток 4800, тестовий БЖ", await store.GetLastTestConfigAsync("160697"));
        Assert.Null(await store.GetLastTestConfigAsync("160698"));
    }
```

(`NewStore()` — вспомогательный метод, который уже есть в этом файле.)

- [ ] **Step 2: Прогнать, убедиться что падают**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~TestRunConfigTests`
Expected: FAIL — `TestRunRequest` и методы стора не существуют.

- [ ] **Step 3: Добавить DTO**

Создать `src/SzDiag.Contracts/TestRunRequest.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>Запуск прогона. Метка конфигурации обязательна: прогон без неё через неделю
/// нечитаем — непонятно, что с чем сравнивать (СЗ 160697: результаты «профиль против стока»
/// потерялись именно так).</summary>
/// <param name="Filter">Фильтр набора тестов (null — весь набор).</param>
/// <param name="Config">Метка конфигурации, напр. «EXPO 6000, штатний БЖ».</param>
/// <param name="SameConfig">Повторить прогон на последней сохранённой метке.</param>
public sealed record TestRunRequest(string? Filter, string? Config, bool SameConfig);
```

- [ ] **Step 4: Добавить хранение метки в стор**

В `src/SzDiag.Hub/ISessionStore.cs`:

```csharp
    /// <summary>Запомнить, в какой конфигурации гнали последний прогон по этой СЗ.</summary>
    Task SetLastTestConfigAsync(string sz, string config, CancellationToken ct = default);

    /// <summary>Метка последнего прогона (null — прогонов ещё не было).</summary>
    Task<string?> GetLastTestConfigAsync(string sz, CancellationToken ct = default);
```

В `src/SzDiag.Hub/SqliteSessionStore.cs` — таблица и реализация, в стиле существующих методов (создание таблицы дописать туда же, где создаются остальные, в `InitializeAsync`):

```sql
CREATE TABLE IF NOT EXISTS test_config (
    sz TEXT PRIMARY KEY,
    config TEXT NOT NULL,
    set_at TEXT NOT NULL
);
```

```csharp
    public async Task SetLastTestConfigAsync(string sz, string config, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO test_config (sz, config, set_at) VALUES ($sz, $config, $at)
            ON CONFLICT(sz) DO UPDATE SET config = excluded.config, set_at = excluded.set_at;
            """;
        cmd.Parameters.AddWithValue("$sz", sz);
        cmd.Parameters.AddWithValue("$config", config);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetLastTestConfigAsync(string sz, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT config FROM test_config WHERE sz = $sz;";
        cmd.Parameters.AddWithValue("$sz", sz);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
```

**Важно:** имя хелпера открытия соединения (`OpenAsync`) и способ создания таблиц взять фактические из `SqliteSessionStore`. Если в тестах есть in-memory реализация `ISessionStore` (искать `: ISessionStore` в `tests/`) — добавить обе операции и туда, иначе тестовый проект не соберётся.

- [ ] **Step 5: Переписать endpoint `test`**

В `src/SzDiag.Hub/ManagementApi.cs` заменить существующий обработчик:

```csharp
        // Метка конфигурации обязательна: см. TestRunRequest. Без неё прогон не стартует —
        // это дешевле, чем через неделю гадать, на профиле гнали или на стоке.
        group.MapPost("/sessions/{sz}/test", async (string sz, TestRunRequest body,
            TestRunTrigger trigger, ISessionStore store, JournalWriter journal) =>
        {
            var config = body.Config?.Trim();
            if (string.IsNullOrWhiteSpace(config) && body.SameConfig)
                config = await store.GetLastTestConfigAsync(sz);

            if (string.IsNullOrWhiteSpace(config))
            {
                var last = await store.GetLastTestConfigAsync(sz);
                var hint = last is null
                    ? "укажите конфигурацию: --config \"EXPO 6000, штатный БП\""
                    : $"прошлый прогон: «{last}» — повторить ту же: --same-config";
                return Results.BadRequest($"прогон без метки конфигурации не запускается; {hint}");
            }

            if (!await trigger.TriggerAsync(sz, body.Filter)) return Results.NotFound();

            await store.SetLastTestConfigAsync(sz, config);
            journal.Command(sz, $"`test run {body.Filter ?? "усе"}` — старт; конфігурація: **{config}**");
            return Results.Ok();
        });
```

- [ ] **Step 6: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~TestRunConfigTests`
Expected: PASS (4 теста).

- [ ] **Step 7: Прогнать весь набор — старый вызов `test` сломан намеренно**

Run: `dotnet test`
Expected: падают тесты, которые дёргали `test` со старой сигнатурой (query-параметр `filter`). Починить их на новое тело `TestRunRequest` — это и есть перевод на новый контракт.

- [ ] **Step 8: Коммит**

```bash
git add src/SzDiag.Contracts/TestRunRequest.cs src/SzDiag.Hub tests/SzDiag.Hub.Tests
git commit -m "feat(hub): прогон только с меткой конфигурации, метка живёт в SQLite и журнале"
```

---

### Task 8: `szcli test run --config` / `--same-config`

**Files:**
- Modify: `src/SzDiag.Cli/IHubApiClient.cs`, `src/SzDiag.Cli/HubApiClient.cs`
- Modify: `src/SzDiag.Cli/Program.cs`
- Create: `src/SzDiag.Cli/TestRunArgs.cs`
- Test: `tests/SzDiag.Cli.Tests/TestRunArgsTests.cs`
- Test (modify): `tests/SzDiag.Cli.Tests/HubApiClientTests.cs`

**Interfaces:**
- Consumes: `TestRunRequest` (Task 7), endpoint из Task 7.
- Produces:
  - `IHubApiClient.TriggerTestAsync(string sz, string? filter, string? config, bool sameConfig, CancellationToken ct = default) : Task<TriggerResult>`, где `record TriggerResult(bool Ok, string? Error)` — CLI обязан печатать текст ошибки от hub, иначе подсказка про `--same-config` до пользователя не доедет.
  - `record TestRunArgs(string? Filter, string? Config, bool SameConfig)` со статическим `Parse(string[] rest)`.

- [ ] **Step 1: Написать падающие тесты**

Разбор флагов выносим в тестируемый класс, а не оставляем в top-level `Program.cs`. Создать `tests/SzDiag.Cli.Tests/TestRunArgsTests.cs`:

```csharp
using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class TestRunArgsTests
{
    [Fact]
    public void Parse_ConfigFlag_TakesNextArgumentAsLabel()
    {
        var args = TestRunArgs.Parse(new[] { "occt", "--config", "EXPO 6000, штатний БЖ" });

        Assert.Equal("occt", args.Filter);
        Assert.Equal("EXPO 6000, штатний БЖ", args.Config);
        Assert.False(args.SameConfig);
    }

    [Fact]
    public void Parse_SameConfigFlag_SetsFlagWithoutLabel()
    {
        var args = TestRunArgs.Parse(new[] { "occt", "--same-config" });

        Assert.True(args.SameConfig);
        Assert.Null(args.Config);
    }

    [Fact]
    public void Parse_NoFilter_LeavesFilterNull()
    {
        var args = TestRunArgs.Parse(new[] { "--config", "сток JEDEC 4800" });

        Assert.Null(args.Filter);
        Assert.Equal("сток JEDEC 4800", args.Config);
    }

    [Fact]
    public void Parse_ConfigWithoutValue_LeavesConfigNull()
    {
        var args = TestRunArgs.Parse(new[] { "occt", "--config" });

        Assert.Null(args.Config);
    }
}
```

Плюс дописать в `HubApiClientTests` проверку самого запроса:

```csharp
    [Fact]
    public async Task TriggerTestAsync_SendsConfigInBody_AndReturnsHubErrorText()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            "прогон без метки конфигурации не запускается; повторить ту же: --same-config");
        var client = NewClient(handler);

        var result = await client.TriggerTestAsync("160697", "occt", "EXPO 6000, штатний БЖ", false);

        Assert.False(result.Ok);
        Assert.Contains("--same-config", result.Error);
        Assert.Contains("EXPO 6000, штатний БЖ",
            await handler.LastRequest!.Content!.ReadAsStringAsync());
    }
```

- [ ] **Step 2: Прогнать, убедиться что падают**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~TestRunArgsTests`
Expected: FAIL — `TestRunArgs` не существует, у `TriggerTestAsync` другая сигнатура.

- [ ] **Step 3: Расширить клиент**

В `src/SzDiag.Cli/IHubApiClient.cs` заменить существующий `TriggerTestAsync`:

```csharp
    Task<TriggerResult> TriggerTestAsync(string sz, string? filter, string? config,
        bool sameConfig, CancellationToken ct = default);
```

Рядом (файл `src/SzDiag.Cli/IHubApiClient.cs`):

```csharp
/// <summary>Итог запуска: hub возвращает текст причины, и CLI обязан его показать —
/// иначе подсказка про `--same-config` не доедет до пользователя.</summary>
public sealed record TriggerResult(bool Ok, string? Error);
```

В `src/SzDiag.Cli/HubApiClient.cs`:

```csharp
    public async Task<TriggerResult> TriggerTestAsync(string sz, string? filter, string? config,
        bool sameConfig, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"/api/sessions/{sz}/test",
            new TestRunRequest(filter, config, sameConfig), ct);
        if (res.IsSuccessStatusCode) return new TriggerResult(true, null);
        var body = await res.Content.ReadAsStringAsync(ct);
        return new TriggerResult(false, string.IsNullOrWhiteSpace(body) ? null : body);
    }
```

- [ ] **Step 4: Вынести разбор флагов и подключить его к команде**

Создать `src/SzDiag.Cli/TestRunArgs.cs`:

```csharp
namespace SzDiag.Cli;

/// <summary>Разбор хвоста `test run &lt;СЗ&gt; …`: фильтр набора плюс метка конфигурации.
/// Вынесено из Program.cs отдельно, чтобы разбор был покрыт тестами (top-level statements
/// напрямую не тестируются).</summary>
public sealed record TestRunArgs(string? Filter, string? Config, bool SameConfig)
{
    public static TestRunArgs Parse(string[] rest)
    {
        string? config = null;
        var sameConfig = false;
        var positional = new List<string>();

        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                // Значение без флага молча не проглатываем: пустая метка — та же потеря контекста.
                if (i + 1 < rest.Length) config = rest[++i];
                continue;
            }
            if (rest[i].Equals("--same-config", StringComparison.OrdinalIgnoreCase))
            {
                sameConfig = true;
                continue;
            }
            positional.Add(rest[i]);
        }

        return new TestRunArgs(positional.Count > 0 ? positional[0] : null, config, sameConfig);
    }
}
```

В `src/SzDiag.Cli/Program.cs` заменить `case "test" when …`:

```csharp
    case "test" when args.Length >= 3 && args[1].Equals("run", StringComparison.OrdinalIgnoreCase):
    {
        var (testFilter, config, sameConfig) = TestRunArgs.Parse(args[3..]);
        var result = await client.TriggerTestAsync(args[2], testFilter, config, sameConfig);
        if (result.Ok)
        {
            var scope = testFilter is null ? "весь набор" : $"фильтр: {testFilter}";
            var label = config is not null ? config : "как в прошлый раз";
            AnsiConsole.MarkupLineInterpolated(
                $"[green]СЗ {args[2]}: прогон запущен[/] ({scope}, конфигурация: {label}) — отчёт появится в kb.");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[2]}: прогон не запущен.[/] {result.Error}");
            return CliErrors.BadArguments;
        }
        break;
    }
```

- [ ] **Step 5: Прогнать тесты CLI**

Run: `dotnet test tests/SzDiag.Cli.Tests`
Expected: PASS — включая починенные существующие тесты `test run`, которые звали старую сигнатуру.

- [ ] **Step 6: Обновить справку CLI**

Строку про `test run` привести к виду:

```
  test run <СЗ> [фильтр] --config "<конфигурация>" | --same-config
                             прогон набора; метка конфигурации обязательна
```

- [ ] **Step 7: Прогнать всё и закоммитить**

Run: `dotnet build; dotnet test`
Expected: PASS.

```bash
git add src/SzDiag.Cli tests/SzDiag.Cli.Tests
git commit -m "feat(cli): test run требует --config или --same-config"
```

---

### Task 9: Метка конфигурации в шапке `report.md`

**Files:**
- Modify: `src/SzDiag.Hub/AgentHub.cs` (метод, принимающий `UploadReportFile`)
- Test: `tests/SzDiag.Hub.Tests/ReportConfigHeaderTests.cs`

**Interfaces:**
- Consumes: `ISessionStore.GetLastTestConfigAsync` (Task 7), существующий `IReportStore.Save(string sz, string timestamp, string fileName, byte[] content) : string`.
- Produces: сохранённый `report.md` содержит строку `**Конфігурація прогону:** <метка>` сразу после заголовка первого уровня.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/ReportConfigHeaderTests.cs` **по образцу существующего `tests/SzDiag.Hub.Tests/ReportUploadIntegrationTests.cs`** — там уже отработана заливка файла отчёта агентом и проверка результата на диске в `kb/СЗ/<sz>/reports/<timestamp>/`:

```csharp
    [Fact]
    public async Task UploadReport_WithStoredConfig_InsertsLabelAfterTitle()
    {
        var store = _factory.Services.GetRequiredService<ISessionStore>();
        await store.SetLastTestConfigAsync("160697", "EXPO 6000, штатний БЖ");

        await UploadAsync("160697", "20260810-170400", "report.md", "# Звіт 160697\n\nтіло\n");

        var saved = File.ReadAllText(Path.Combine(_kbRoot, "СЗ", "160697", "reports",
            "20260810-170400", "report.md"));
        Assert.StartsWith("# Звіт 160697", saved);
        Assert.Contains("**Конфігурація прогону:** EXPO 6000, штатний БЖ", saved);
        Assert.Contains("тіло", saved);
    }

    [Fact]
    public async Task UploadReport_NonMarkdownFile_LeftUntouched()
    {
        var store = _factory.Services.GetRequiredService<ISessionStore>();
        await store.SetLastTestConfigAsync("160697", "EXPO 6000, штатний БЖ");

        await UploadAsync("160697", "20260810-170400", "sensors.csv", "time,cpu\n");

        var saved = File.ReadAllText(Path.Combine(_kbRoot, "СЗ", "160697", "reports",
            "20260810-170400", "sensors.csv"));
        Assert.DoesNotContain("Конфігурація", saved);
    }

    [Fact]
    public async Task UploadReport_WithoutStoredConfig_LeftUntouched()
    {
        await UploadAsync("160705", "20260810-170400", "report.md", "# Звіт 160705\n\nтіло\n");

        var saved = File.ReadAllText(Path.Combine(_kbRoot, "СЗ", "160705", "reports",
            "20260810-170400", "report.md"));
        Assert.DoesNotContain("Конфігурація", saved);
    }
```

**Важно:** `UploadAsync` — обёртка над реальным вызовом агента (`UploadReportFile`), собранная так же, как в `ReportUploadIntegrationTests`; точное имя метода и его параметры взять из `src/SzDiag.Hub/AgentHub.cs`.

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~ReportConfigHeaderTests`
Expected: FAIL — метка в отчёт не попадает.

- [ ] **Step 3: Дописать вставку метки**

В `src/SzDiag.Hub/AgentHub.cs`, в обработчике загрузки файла, перед `_reports.Save(...)`:

```csharp
        // Отчёт собирает агент, а метку конфигурации знает только хост — дописываем здесь.
        // Без неё через неделю непонятно, на профиле гнали или на стоке (СЗ 160697).
        if (fileName.Equals("report.md", StringComparison.OrdinalIgnoreCase)
            && await _store.GetLastTestConfigAsync(sz) is { } config)
        {
            var text = Encoding.UTF8.GetString(content);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var cut = text.IndexOf(nl, StringComparison.Ordinal);
            content = Encoding.UTF8.GetBytes(cut < 0
                ? $"{text}{nl}{nl}**Конфігурація прогону:** {config}{nl}"
                : $"{text[..cut]}{nl}{nl}**Конфігурація прогону:** {config}{text[cut..]}");
        }
```

- [ ] **Step 4: Прогнать тесты, убедиться что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~ReportConfigHeaderTests`
Expected: PASS.

- [ ] **Step 5: Прогнать весь набор**

Run: `dotnet test`
Expected: PASS (~481 тест + новые).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hub/AgentHub.cs tests/SzDiag.Hub.Tests/ReportConfigHeaderTests.cs
git commit -m "feat(hub): метка конфигурации в шапке report.md"
```

---

### Task 10: Документация и живой прогон

**Files:**
- Modify: `CLAUDE.md` (раздел «Команды» / описание `SzDiag.Cli`)
- Modify: `docs/dev-knowledge-base.md`
- Modify: `docs/dev-backlog.md`

**Interfaces:**
- Consumes: всё, что сделано в задачах 1–9.
- Produces: описанный контур журнала в документации проекта.

- [ ] **Step 1: Записать боль в бэклог**

В `docs/dev-backlog.md` добавить пункт в общем формате файла (номер — следующий свободный):

```markdown
### N. Потеря контекста между сессиями — СЗ 160697 (2026-08-10 → 2026-08-19)

**Боль.** Мастер снял штатный БП Gigabyte UD850GM и поставил тестовый, EXPO в BIOS
трогали или нет — неизвестно, результат прогона на тестовом БП неизвестен. В kb — ни строки:
последняя правка vault по СЗ `5ab98ea` от 10.08 17:38, дальше 9 дней тишины при открытой
заявке и замороженном WU (`dist\host\cli\freeze\160697.json` на месте). Результаты
дискриминатора «профиль против стока» утрачены, тестировать заново.

**Решение:** журнал СЗ — спека `docs/superpowers/specs/2026-08-19-sz-journal-design.md`,
план `docs/superpowers/plans/2026-08-19-sz-journal.md`. Фазы 1–2 (журнал, `szcli note`,
обязательный `--config`) — этот план; фазы 3–4 (автоснапшот железа, `szcli todo`,
гейт в `close`) — следующий.
```

- [ ] **Step 2: Описать новые команды в `CLAUDE.md`**

В описании `SzDiag.Cli` добавить абзац:

```markdown
  `szcli note <СЗ> "<текст>"` — ручной шаг у машины (свап железа, правка BIOS, осмотр) сразу
  в журнал СЗ. Принимается **даже когда сессия offline или закрыта**: физический шаг
  фиксируется в момент, когда он сделан, иначе теряется вместе с сессией (СЗ 160697).
  Журнал — `kb/СЗ/<номер>/журнал.md`, туда же автоматически падают команды CLI и события
  машины (вырубон, потеря связи). `дії.md` остаётся человеческим пересказом поверх журнала.
  `szcli test run <СЗ> [фильтр] --config "<конфигурация>"` — **метка обязательна**, без неё
  прогон не стартует; повтор той же конфигурации — `--same-config`.
```

- [ ] **Step 3: Обновить карту функционала**

В `docs/dev-knowledge-base.md` добавить `POST /api/sessions/{sz}/journal` в таблицу `/api` и новое тело `TestRunRequest` у `POST /api/sessions/{sz}/test` (описание полей — из `src/SzDiag.Contracts/TestRunRequest.cs`).

- [ ] **Step 4: Собрать dist и проверить руками**

Run: `.\tools\build-dist.ps1`
Затем поднять hub (`dist\host\start-hub.cmd`) и на закрытой/несуществующей сессии выполнить:

```powershell
.\dist\host\szcli.cmd note 160697 "тестова нотатка: свап БЖ"
```

Expected: `СЗ 160697: записано в журнал`, в `dist\host\kb\СЗ\160697\журнал.md` появилась строка `- **HH:mm** ✋ тестова нотатка: свап БЖ`.

- [ ] **Step 5: Проверить гейт конфигурации**

```powershell
.\dist\host\szcli.cmd test run 160697 occt
```

Expected: отказ с текстом про `--config` (или про `--same-config`, если метка уже сохранена), прогон не стартовал.

- [ ] **Step 6: Коммит**

```bash
git add CLAUDE.md docs/dev-backlog.md docs/dev-knowledge-base.md
git commit -m "docs: журнал СЗ — команды, endpoint и боль 160697 в бэклоге"
```

---

## Что остаётся на следующий план (фазы 3–4)

- Секция `snapshot` в агенте (JSON-снимок конфигурации), запись в `kb/СЗ/<номер>/snapshots/`, дифф в журнал (`RAM Configured 6000 → 4800 (EXPO вимкнено)`).
- `GET /api/sessions/stale`, `szcli todo`, красная строка в `list`/`watch` для зависших СЗ (включая offline), `Hub.StaleSzHours`.
- Гейт в `szcli close`: скелетный `висновок.md` или непустая очередь «Далі» → отказ, обход `--force "причина"` с записью в журнал.
