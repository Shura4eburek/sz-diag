# sz-diag Фаза 2 — план: автоматизация базы знаний + поиск

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Библиотека `SzDiag.Kb` и команды `szcli kb record`/`kb search` для наполнения базы знаний структурированными данными (заказ/устройство/дефект/замены/находки) и поиска по прошлым СЗ (по заказу и свободному тексту), с автосозданием связуемых заметок и Dataview-шаблонами для Obsidian.

**Architecture:** Вся логика КЗ — в новой библиотеке `SzDiag.Kb` (пути, правка frontmatter, автосоздание заметок, запись, поиск). `KnowledgeBaseScaffolder` переносится из `SzDiag.Hub` в `SzDiag.Kb`; hub и cli ссылаются на неё. `szcli` пишет/читает локальную ФС vault напрямую (hub не задействован).

**Tech Stack:** .NET 8, C#, xUnit. Без внешних зависимостей (свой лёгкий парсер frontmatter — формат наш).

**Предпосылка:** реализованы Фазы 1 (hub, cli, agent). Спека: [../specs/2026-07-01-phase2-kb-automation-design.md](../specs/2026-07-01-phase2-kb-automation-design.md), [../specs/2026-07-01-kb-obsidian-design.md](../specs/2026-07-01-kb-obsidian-design.md).

---

## File Structure

```
src/SzDiag.Kb/                (новая библиотека)
  SzDiag.Kb.csproj
  KbPaths.cs                  — пути к заметкам (СЗ/<n>/, Заказы/, Дефекты/, …)
  FrontmatterEditor.cs        — чтение/правка YAML-frontmatter home-заметки
  IKnowledgeBaseScaffolder.cs — ПЕРЕНОС из SzDiag.Hub
  KnowledgeBaseScaffolder.cs  — ПЕРЕНОС из SzDiag.Hub (рефактор под KbPaths)
  EntityNoteWriter.cs         — автосоздание Заказ/Дефект/Компонент/Устройство + MOC (с Dataview)
  RecordRequest.cs            — DTO входных данных record
  KbRecorder.cs               — merge-логика record
  KbSearchResult.cs           — DTO результата поиска
  KbSearcher.cs               — поиск по заказу и тексту
src/SzDiag.Hub/               — ссылка на SzDiag.Kb; scaffolder-файлы удалены; usings обновлены
src/SzDiag.Cli/               — ссылка на SzDiag.Kb; команды kb record/search; KbRoot в CliOptions
tests/SzDiag.Kb.Tests/        — юниты на temp-каталогах
```

---

### Task 1: Библиотека SzDiag.Kb, KbPaths и перенос scaffolder

**Files:**
- Create: `src/SzDiag.Kb/SzDiag.Kb.csproj`, `src/SzDiag.Kb/KbPaths.cs`, `src/SzDiag.Kb/IKnowledgeBaseScaffolder.cs`, `src/SzDiag.Kb/KnowledgeBaseScaffolder.cs`, `tests/SzDiag.Kb.Tests/SzDiag.Kb.Tests.csproj`, `tests/SzDiag.Kb.Tests/KnowledgeBaseScaffolderTests.cs`, `tests/SzDiag.Kb.Tests/KbPathsTests.cs`
- Delete: `src/SzDiag.Hub/IKnowledgeBaseScaffolder.cs`, `src/SzDiag.Hub/KnowledgeBaseScaffolder.cs`, `tests/SzDiag.Hub.Tests/KnowledgeBaseScaffolderTests.cs`
- Modify: `src/SzDiag.Hub/AgentHub.cs`, `src/SzDiag.Hub/Program.cs` (добавить `using SzDiag.Kb;`)

- [ ] **Step 1: Создать проекты и ссылки**

Run:
```bash
dotnet new classlib -n SzDiag.Kb -o src/SzDiag.Kb -f net8.0
dotnet new xunit -n SzDiag.Kb.Tests -o tests/SzDiag.Kb.Tests -f net8.0
dotnet sln add src/SzDiag.Kb tests/SzDiag.Kb.Tests
dotnet add tests/SzDiag.Kb.Tests reference src/SzDiag.Kb
dotnet add src/SzDiag.Hub reference src/SzDiag.Kb
dotnet add tests/SzDiag.Hub.Tests package Microsoft.AspNetCore.Mvc.Testing --version 8.0.11
rm src/SzDiag.Kb/Class1.cs
rm tests/SzDiag.Kb.Tests/UnitTest1.cs
```
(Пакет Mvc.Testing уже есть в Hub.Tests — команда идемпотентна, оставлена для полноты.)

- [ ] **Step 2: KbPaths**

`src/SzDiag.Kb/KbPaths.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Пути к заметкам Obsidian-vault базы знаний. Все имена папок — здесь.</summary>
public sealed class KbPaths
{
    public string Root { get; }
    public KbPaths(string root) => Root = root;

    public string SzRoot => Path.Combine(Root, "СЗ");
    public string SzDir(string sz) => Path.Combine(SzRoot, sz);
    public string HomeNote(string sz) => Path.Combine(SzDir(sz), $"{sz}.md");
    public string Request(string sz) => Path.Combine(SzDir(sz), "request.md");
    public string Findings(string sz) => Path.Combine(SzDir(sz), "findings.md");
    public string Actions(string sz) => Path.Combine(SzDir(sz), "actions.md");
    public string LogsDir(string sz) => Path.Combine(SzDir(sz), "logs");

    public string OrderNote(string order) => Path.Combine(Root, "Заказы", $"{order}.md");
    public string DefectNote(string defect) => Path.Combine(Root, "Дефекты", $"{defect}.md");
    public string ComponentNote(string comp) => Path.Combine(Root, "Компоненты", $"{comp}.md");
    public string DeviceNote(string device) => Path.Combine(Root, "Устройства", $"{device}.md");
    public string Moc => Path.Combine(Root, "MOC.md");
}
```

- [ ] **Step 3: Написать тест KbPaths**

`tests/SzDiag.Kb.Tests/KbPathsTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbPathsTests
{
    [Fact]
    public void HomeNote_IsSzFolderPlusSzMd()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "СЗ", "156864", "156864.md"), p.HomeNote("156864"));
    }

    [Fact]
    public void EntityNotes_UnderNamedFolders()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "Заказы", "A-1.md"), p.OrderNote("A-1"));
        Assert.Equal(Path.Combine("/vault", "Компоненты", "SSD.md"), p.ComponentNote("SSD"));
    }
}
```

- [ ] **Step 4: Перенести scaffolder в SzDiag.Kb (рефактор под KbPaths)**

`src/SzDiag.Kb/IKnowledgeBaseScaffolder.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Создаёт каркас папки базы знаний для СЗ в Obsidian-форме.</summary>
public interface IKnowledgeBaseScaffolder
{
    /// <summary>Создаёт kb/СЗ/&lt;sz&gt;/ если её ещё нет. Возвращает путь к папке СЗ.</summary>
    string EnsureSkeleton(string sz);
}
```

`src/SzDiag.Kb/KnowledgeBaseScaffolder.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>
/// Создаёт каркас kb/СЗ/&lt;sz&gt;/ в Obsidian-форме. Идемпотентно: если папка СЗ
/// уже есть — ничего не трогает (данные диагностики не перетираются).
/// </summary>
public sealed class KnowledgeBaseScaffolder : IKnowledgeBaseScaffolder
{
    private readonly KbPaths _paths;
    private readonly Func<DateTimeOffset> _now;

    public KnowledgeBaseScaffolder(string kbRoot, Func<DateTimeOffset>? now = null)
    {
        _paths = new KbPaths(kbRoot);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string EnsureSkeleton(string sz)
    {
        var dir = _paths.SzDir(sz);
        if (Directory.Exists(dir)) return dir;

        Directory.CreateDirectory(_paths.LogsDir(sz));

        var date = _now().ToString("yyyy-MM-dd");
        WriteIfMissing(_paths.HomeNote(sz), HomeNote(sz, date));
        WriteIfMissing(_paths.Request(sz), $"# Дефект (со слов клиента) — СЗ {sz}\n\n");
        WriteIfMissing(_paths.Findings(sz), $"# Диагностика — СЗ {sz}\n\n");
        WriteIfMissing(_paths.Actions(sz), $"# Что заменили / сделали — СЗ {sz}\n\n");
        return dir;
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    private static string HomeNote(string sz, string date) =>
        $"""
        ---
        сз: {sz}
        заказ: ""
        дефект: []
        заменено: []
        устройство: ""
        дата: {date}
        ---

        # СЗ {sz}

        ## Дефект
        ![[request]]

        ## Диагностика
        ![[findings]]

        ## Замены
        ![[actions]]

        """;
}
```

- [ ] **Step 5: Удалить старые файлы scaffolder из hub и обновить usings**

Run:
```bash
rm src/SzDiag.Hub/IKnowledgeBaseScaffolder.cs
rm src/SzDiag.Hub/KnowledgeBaseScaffolder.cs
rm tests/SzDiag.Hub.Tests/KnowledgeBaseScaffolderTests.cs
```

В `src/SzDiag.Hub/AgentHub.cs` добавить строку using после `using SzDiag.Contracts;`:
```csharp
using SzDiag.Kb;
```

В `src/SzDiag.Hub/Program.cs` добавить после `using SzDiag.Contracts;`:
```csharp
using SzDiag.Kb;
```

- [ ] **Step 6: Перенести тест scaffolder в Kb.Tests**

`tests/SzDiag.Kb.Tests/KnowledgeBaseScaffolderTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KnowledgeBaseScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-{Guid.NewGuid():N}");

    private KnowledgeBaseScaffolder NewScaffolder()
        => new(_root, () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void EnsureSkeleton_CreatesExpectedFiles()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");

        Assert.True(File.Exists(Path.Combine(dir, "156864.md")));
        Assert.True(File.Exists(Path.Combine(dir, "request.md")));
        Assert.True(File.Exists(Path.Combine(dir, "findings.md")));
        Assert.True(File.Exists(Path.Combine(dir, "actions.md")));
        Assert.True(Directory.Exists(Path.Combine(dir, "logs")));
    }

    [Fact]
    public void HomeNote_ContainsFrontmatterWithSzAndAutoDate()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");

        var home = File.ReadAllText(Path.Combine(dir, "156864.md"));
        Assert.Contains("сз: 156864", home);
        Assert.Contains("дата: 2026-07-01", home);
        Assert.Contains("![[request]]", home);
    }

    [Fact]
    public void EnsureSkeleton_ExistingDir_DoesNotOverwrite()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");
        var reqPath = Path.Combine(dir, "request.md");
        File.WriteAllText(reqPath, "РУЧНОЙ ТЕКСТ");

        s.EnsureSkeleton("156864");

        Assert.Equal("РУЧНОЙ ТЕКСТ", File.ReadAllText(reqPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 7: Собрать и прогнать все тесты (регресс hub + новые)**

Run: `dotnet build`
Expected: Build succeeded (hub видит scaffolder из SzDiag.Kb через using).
Run: `dotnet test`
Expected: PASS — все тесты, включая перенесённый scaffolder (в Kb.Tests) и прежние hub-тесты.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(kb): библиотека SzDiag.Kb, KbPaths, перенос scaffolder из hub"
```

---

### Task 2: FrontmatterEditor

**Files:**
- Create: `src/SzDiag.Kb/FrontmatterEditor.cs`
- Test: `tests/SzDiag.Kb.Tests/FrontmatterEditorTests.cs`

Правит frontmatter home-заметки. Скаляры и inline-списки хранятся как «сырые» токены
(включая кавычки/скобки), т.к. форматирование wikilink делает вызывающий код.

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Kb.Tests/FrontmatterEditorTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class FrontmatterEditorTests
{
    private const string Sample =
        "---\nсз: 156864\nзаказ: \"\"\nдефект: []\nзаменено: []\nустройство: \"\"\nдата: 2026-07-01\n---\n\n# СЗ 156864\n\n![[request]]\n";

    [Fact]
    public void SetScalar_OverwritesValue_AndSerializes()
    {
        var fm = FrontmatterEditor.Load(Sample);
        fm.SetScalar("заказ", "\"[[A-2025-0098]]\"");

        var outText = fm.Serialize();

        Assert.Contains("заказ: \"[[A-2025-0098]]\"", outText);
        Assert.Contains("# СЗ 156864", outText);   // тело сохранено
    }

    [Fact]
    public void AddToList_AppendsWithoutDuplicates()
    {
        var fm = FrontmatterEditor.Load(Sample);
        fm.AddToList("дефект", "\"[[Не стартует POST]]\"");
        fm.AddToList("дефект", "\"[[Не стартует POST]]\""); // дубль

        Assert.Single(fm.GetList("дефект"));
        Assert.Contains("дефект: [\"[[Не стартует POST]]\"]", fm.Serialize());
    }

    [Fact]
    public void AddToList_ParsesExistingItems()
    {
        var withOne = Sample.Replace("дефект: []", "дефект: [\"[[A]]\"]");
        var fm = FrontmatterEditor.Load(withOne);
        fm.AddToList("дефект", "\"[[B]]\"");

        Assert.Equal(2, fm.GetList("дефект").Count);
        Assert.Contains("дефект: [\"[[A]]\", \"[[B]]\"]", fm.Serialize());
    }

    [Fact]
    public void GetScalar_ReturnsRawValue()
    {
        var fm = FrontmatterEditor.Load(Sample.Replace("заказ: \"\"", "заказ: \"[[A-1]]\""));
        Assert.Equal("\"[[A-1]]\"", fm.GetScalar("заказ"));
    }

    [Fact]
    public void UnknownKeysAndBody_Preserved()
    {
        var withExtra = Sample.Replace("дата: 2026-07-01", "дата: 2026-07-01\nтег: важное");
        var fm = FrontmatterEditor.Load(withExtra);
        fm.SetScalar("устройство", "\"[[Lenovo]]\"");

        var outText = fm.Serialize();
        Assert.Contains("тег: важное", outText);
        Assert.Contains("![[request]]", outText);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FrontmatterEditorTests`
Expected: FAIL — `FrontmatterEditor` не существует.

- [ ] **Step 3: Реализовать FrontmatterEditor**

`src/SzDiag.Kb/FrontmatterEditor.cs`:
```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace SzDiag.Kb;

/// <summary>
/// Лёгкая правка frontmatter (блок между первыми `---`). Скаляры хранятся как сырое
/// значение строки; списки — как токены inline-массива. Тело и неизвестные ключи
/// сохраняются. Формат наш, поэтому без внешнего YAML.
/// </summary>
public sealed class FrontmatterEditor
{
    private sealed class Entry
    {
        public required string Key;
        public string? Scalar;          // сырое значение (для скаляра)
        public List<string>? List;      // токены (для списка)
        public bool IsList => List is not null;
    }

    private readonly List<Entry> _entries;
    private readonly string _body;

    private FrontmatterEditor(List<Entry> entries, string body)
    {
        _entries = entries;
        _body = body;
    }

    public static FrontmatterEditor Load(string content)
    {
        var entries = new List<Entry>();
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n"))
            return new FrontmatterEditor(entries, normalized);

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return new FrontmatterEditor(entries, normalized);

        var fmBlock = normalized.Substring(4, end - 4);
        var body = normalized.Substring(end + 4).TrimStart('\n');

        foreach (var line in fmBlock.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var key = line.Substring(0, idx).Trim();
            var val = line.Substring(idx + 1).Trim();
            if (val.StartsWith("["))
                entries.Add(new Entry { Key = key, List = ParseList(val) });
            else
                entries.Add(new Entry { Key = key, Scalar = val });
        }
        return new FrontmatterEditor(entries, body);
    }

    private static List<string> ParseList(string bracketed)
    {
        var items = new List<string>();
        foreach (Match m in Regex.Matches(bracketed, "\"(?:[^\"\\\\]|\\\\.)*\""))
            items.Add(m.Value);
        return items;
    }

    public string? GetScalar(string key)
        => _entries.FirstOrDefault(e => e.Key == key && !e.IsList)?.Scalar;

    public IReadOnlyList<string> GetList(string key)
        => _entries.FirstOrDefault(e => e.Key == key && e.IsList)?.List ?? (IReadOnlyList<string>)Array.Empty<string>();

    public void SetScalar(string key, string rawValue)
    {
        var e = _entries.FirstOrDefault(x => x.Key == key);
        if (e is null) { _entries.Add(new Entry { Key = key, Scalar = rawValue }); return; }
        e.Scalar = rawValue;
        e.List = null;
    }

    public void AddToList(string key, string rawItem)
    {
        var e = _entries.FirstOrDefault(x => x.Key == key);
        if (e is null) { _entries.Add(new Entry { Key = key, List = new List<string> { rawItem } }); return; }
        e.List ??= new List<string>();
        e.Scalar = null;
        if (!e.List.Contains(rawItem)) e.List.Add(rawItem);
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (var e in _entries)
        {
            if (e.IsList)
                sb.Append($"{e.Key}: [{string.Join(", ", e.List!)}]\n");
            else
                sb.Append($"{e.Key}: {e.Scalar}\n");
        }
        sb.Append("---\n\n");
        sb.Append(_body);
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FrontmatterEditorTests`
Expected: PASS (5 тестов).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Kb/FrontmatterEditor.cs tests/SzDiag.Kb.Tests/FrontmatterEditorTests.cs
git commit -m "feat(kb): FrontmatterEditor — правка frontmatter home-заметки"
```

---

### Task 3: EntityNoteWriter (со встроенным Dataview)

**Files:**
- Create: `src/SzDiag.Kb/EntityNoteWriter.cs`
- Test: `tests/SzDiag.Kb.Tests/EntityNoteWriterTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Kb.Tests/EntityNoteWriterTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class EntityNoteWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szent-{Guid.NewGuid():N}");
    private EntityNoteWriter NewWriter() => new(new KbPaths(_root));

    [Fact]
    public void EnsureOrder_CreatesNoteWithDataview()
    {
        var w = NewWriter();
        w.EnsureOrder("A-2025-0098");

        var text = File.ReadAllText(new KbPaths(_root).OrderNote("A-2025-0098"));
        Assert.Contains("# Заказ A-2025-0098", text);
        Assert.Contains("```dataview", text);
    }

    [Fact]
    public void EnsureComponent_ExistingNote_NotOverwritten()
    {
        var w = NewWriter();
        var path = new KbPaths(_root).ComponentNote("SSD");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "РУЧНОЙ");

        w.EnsureComponent("SSD");

        Assert.Equal("РУЧНОЙ", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureMoc_CreatesMoc()
    {
        var w = NewWriter();
        w.EnsureMoc();
        Assert.True(File.Exists(new KbPaths(_root).Moc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter EntityNoteWriterTests`
Expected: FAIL — `EntityNoteWriter` не существует.

- [ ] **Step 3: Реализовать EntityNoteWriter**

`src/SzDiag.Kb/EntityNoteWriter.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Автосоздание связуемых заметок (Заказ/Дефект/Компонент/Устройство) и MOC.
/// Идемпотентно: существующие заметки не перетираются. В шаблоны встроены Dataview-блоки.</summary>
public sealed class EntityNoteWriter
{
    private readonly KbPaths _paths;
    public EntityNoteWriter(KbPaths paths) => _paths = paths;

    public void EnsureOrder(string order) => Ensure(_paths.OrderNote(order),
        $"""
        # Заказ {order}

        Все СЗ по этому заказу:

        ```dataview
        table устройство as "Устройство", дефект as "Дефект", заменено as "Заменено"
        from "СЗ"
        where заказ = this.file.link
        sort дата desc
        ```
        """);

    public void EnsureDefect(string defect) => Ensure(_paths.DefectNote(defect),
        $"""
        # Дефект: {defect}

        Похожие случаи и что помогло:

        ```dataview
        table заказ as "Заказ", заменено as "Заменено"
        from "СЗ"
        where contains(дефект, this.file.link)
        sort дата desc
        ```
        """);

    public void EnsureComponent(string comp) => Ensure(_paths.ComponentNote(comp),
        $"""
        # Компонент: {comp}

        По каким СЗ менялся:

        ```dataview
        table заказ as "Заказ", дефект as "Дефект"
        from "СЗ"
        where contains(заменено, this.file.link)
        sort дата desc
        ```
        """);

    public void EnsureDevice(string device) => Ensure(_paths.DeviceNote(device),
        $"""
        # Устройство: {device}

        СЗ по этой модели:

        ```dataview
        table заказ as "Заказ", дефект as "Дефект", заменено as "Заменено"
        from "СЗ"
        where устройство = this.file.link
        sort дата desc
        ```
        """);

    public void EnsureMoc() => Ensure(_paths.Moc,
        """
        # База знаний — карта

        ## Последние СЗ
        ```dataview
        table заказ, дефект, заменено
        from "СЗ"
        sort дата desc
        limit 20
        ```

        ## Топ дефектов
        ```dataview
        table length(rows) as "Кол-во СЗ"
        from "СЗ"
        flatten дефект as d
        group by d
        sort length(rows) desc
        ```
        """);

    private static void Ensure(string path, string content)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter EntityNoteWriterTests`
Expected: PASS (3 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Kb/EntityNoteWriter.cs tests/SzDiag.Kb.Tests/EntityNoteWriterTests.cs
git commit -m "feat(kb): EntityNoteWriter — автосоздание связуемых заметок с Dataview"
```

---

### Task 4: KbRecorder (merge-логика record)

**Files:**
- Create: `src/SzDiag.Kb/RecordRequest.cs`, `src/SzDiag.Kb/KbRecorder.cs`
- Test: `tests/SzDiag.Kb.Tests/KbRecorderTests.cs`

- [ ] **Step 1: Написать DTO входа**

`src/SzDiag.Kb/RecordRequest.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Входные данные для kb record. Все поля опциональны (merge).</summary>
public sealed class RecordRequest
{
    public required string Sz { get; init; }
    public string? Order { get; init; }
    public string? Device { get; init; }
    public IReadOnlyList<string> Defects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Replaced { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Kb.Tests/KbRecorderTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szrec-{Guid.NewGuid():N}");
    private readonly KbPaths _paths;

    public KbRecorderTests() => _paths = new KbPaths(_root);

    private KbRecorder NewRecorder()
    {
        var scaffolder = new KnowledgeBaseScaffolder(_root,
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        return new KbRecorder(_paths, scaffolder, new EntityNoteWriter(_paths),
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Record_NewSz_CreatesSkeletonAndFillsFrontmatter()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Order = "A-2025-0098",
            Device = "Lenovo IdeaPad 3",
            Defects = new[] { "Не стартует POST" },
            Replaced = new[] { "Kingston A400 240GB" }
        });

        var home = File.ReadAllText(_paths.HomeNote("156864"));
        Assert.Contains("заказ: \"[[A-2025-0098]]\"", home);
        Assert.Contains("устройство: \"[[Lenovo IdeaPad 3]]\"", home);
        Assert.Contains("дефект: [\"[[Не стартует POST]]\"]", home);
        Assert.Contains("заменено: [\"[[Kingston A400 240GB]]\"]", home);
    }

    [Fact]
    public void Record_CreatesLinkedEntityNotes()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Order = "A-1",
            Defects = new[] { "Перегрев" },
            Replaced = new[] { "Кулер" }
        });

        Assert.True(File.Exists(_paths.OrderNote("A-1")));
        Assert.True(File.Exists(_paths.DefectNote("Перегрев")));
        Assert.True(File.Exists(_paths.ComponentNote("Кулер")));
    }

    [Fact]
    public void Record_AppendsFindingsAndActionsWithDate()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Findings = new[] { "Нет видеосигнала" },
            Actions = new[] { "Заменён SSD" }
        });

        Assert.Contains("- 2026-07-01: Нет видеосигнала", File.ReadAllText(_paths.Findings("156864")));
        Assert.Contains("- 2026-07-01: Заменён SSD", File.ReadAllText(_paths.Actions("156864")));
    }

    [Fact]
    public void Record_Twice_IsIdempotent()
    {
        var rec = NewRecorder();
        var req = new RecordRequest
        {
            Sz = "156864",
            Defects = new[] { "Не стартует POST" },
            Findings = new[] { "Нет видеосигнала" }
        };
        rec.Record(req);
        rec.Record(req);

        var home = File.ReadAllText(_paths.HomeNote("156864"));
        Assert.Equal("дефект: [\"[[Не стартует POST]]\"]",
            home.Split('\n').Single(l => l.StartsWith("дефект:")));

        var findings = File.ReadAllText(_paths.Findings("156864"));
        var count = findings.Split("- 2026-07-01: Нет видеосигнала").Length - 1;
        Assert.Equal(1, count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter KbRecorderTests`
Expected: FAIL — `KbRecorder` не существует.

- [ ] **Step 4: Реализовать KbRecorder**

`src/SzDiag.Kb/KbRecorder.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Merge-запись данных СЗ в базу знаний: frontmatter, связуемые заметки, проза.</summary>
public sealed class KbRecorder
{
    private readonly KbPaths _paths;
    private readonly IKnowledgeBaseScaffolder _scaffolder;
    private readonly EntityNoteWriter _entities;
    private readonly Func<DateTimeOffset> _now;

    public KbRecorder(KbPaths paths, IKnowledgeBaseScaffolder scaffolder,
        EntityNoteWriter entities, Func<DateTimeOffset>? now = null)
    {
        _paths = paths;
        _scaffolder = scaffolder;
        _entities = entities;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Record(RecordRequest req)
    {
        _scaffolder.EnsureSkeleton(req.Sz);
        _entities.EnsureMoc();

        var homePath = _paths.HomeNote(req.Sz);
        var fm = FrontmatterEditor.Load(File.ReadAllText(homePath));

        if (req.Order is not null)
        {
            fm.SetScalar("заказ", Quoted(req.Order));
            _entities.EnsureOrder(req.Order);
        }
        if (req.Device is not null)
        {
            fm.SetScalar("устройство", Quoted(req.Device));
            _entities.EnsureDevice(req.Device);
        }
        foreach (var d in req.Defects)
        {
            fm.AddToList("дефект", Quoted(d));
            _entities.EnsureDefect(d);
        }
        foreach (var c in req.Replaced)
        {
            fm.AddToList("заменено", Quoted(c));
            _entities.EnsureComponent(c);
        }
        File.WriteAllText(homePath, fm.Serialize());

        var date = _now().ToString("yyyy-MM-dd");
        foreach (var f in req.Findings) AppendLineIfMissing(_paths.Findings(req.Sz), $"- {date}: {f}");
        foreach (var a in req.Actions) AppendLineIfMissing(_paths.Actions(req.Sz), $"- {date}: {a}");
    }

    private static string Quoted(string name) => $"\"[[{name}]]\"";

    private static void AppendLineIfMissing(string path, string line)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        if (existing.Contains(line)) return;
        var prefix = existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "";
        File.AppendAllText(path, prefix + line + "\n");
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter KbRecorderTests`
Expected: PASS (4 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Kb/RecordRequest.cs src/SzDiag.Kb/KbRecorder.cs tests/SzDiag.Kb.Tests/KbRecorderTests.cs
git commit -m "feat(kb): KbRecorder — merge-запись СЗ (frontmatter, связи, проза)"
```

---

### Task 5: KbSearcher (поиск по заказу и тексту)

**Files:**
- Create: `src/SzDiag.Kb/KbSearchResult.cs`, `src/SzDiag.Kb/KbSearcher.cs`
- Test: `tests/SzDiag.Kb.Tests/KbSearcherTests.cs`

- [ ] **Step 1: Написать DTO результата**

`src/SzDiag.Kb/KbSearchResult.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Найденная СЗ с кратким резюме (сырые значения frontmatter).</summary>
public sealed record KbSearchResult(
    string Sz,
    string Order,
    IReadOnlyList<string> Defects,
    IReadOnlyList<string> Replaced);
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Kb.Tests/KbSearcherTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbSearcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szsearch-{Guid.NewGuid():N}");
    private readonly KbPaths _paths;

    public KbSearcherTests()
    {
        _paths = new KbPaths(_root);
        var scaffolder = new KnowledgeBaseScaffolder(_root,
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var rec = new KbRecorder(_paths, scaffolder, new EntityNoteWriter(_paths),
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        rec.Record(new RecordRequest { Sz = "156864", Order = "A-1",
            Defects = new[] { "Не стартует POST" }, Findings = new[] { "нет видеосигнала" } });
        rec.Record(new RecordRequest { Sz = "156900", Order = "A-1",
            Defects = new[] { "Перегрев" } });
        rec.Record(new RecordRequest { Sz = "157000", Order = "B-2",
            Defects = new[] { "Синий экран" } });
    }

    [Fact]
    public void Search_ByOrder_ReturnsMatchingSz()
    {
        var results = new KbSearcher(_paths).Search(order: "A-1", text: null);
        Assert.Equal(new[] { "156864", "156900" }, results.Select(r => r.Sz).ToArray());
    }

    [Fact]
    public void Search_ByText_MatchesNoteContent()
    {
        var results = new KbSearcher(_paths).Search(order: null, text: "видеосигнал");
        var r = Assert.Single(results);
        Assert.Equal("156864", r.Sz);
    }

    [Fact]
    public void Search_OrderAndText_CombinedAnd()
    {
        var results = new KbSearcher(_paths).Search(order: "A-1", text: "перегрев");
        var r = Assert.Single(results);
        Assert.Equal("156900", r.Sz);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(new KbSearcher(_paths).Search(order: "Z-9", text: null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter KbSearcherTests`
Expected: FAIL — `KbSearcher` не существует.

- [ ] **Step 4: Реализовать KbSearcher**

`src/SzDiag.Kb/KbSearcher.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Поиск по прошлым СЗ: по номеру заказа (frontmatter) и/или свободному тексту.</summary>
public sealed class KbSearcher
{
    private readonly KbPaths _paths;
    public KbSearcher(KbPaths paths) => _paths = paths;

    public IReadOnlyList<KbSearchResult> Search(string? order, string? text)
    {
        var results = new List<KbSearchResult>();
        if (!Directory.Exists(_paths.SzRoot)) return results;

        foreach (var dir in Directory.GetDirectories(_paths.SzRoot))
        {
            var sz = Path.GetFileName(dir);
            var homePath = _paths.HomeNote(sz);
            if (!File.Exists(homePath)) continue;

            var fm = FrontmatterEditor.Load(File.ReadAllText(homePath));

            if (order is not null)
            {
                var orderRaw = fm.GetScalar("заказ") ?? "";
                if (!orderRaw.Contains($"[[{order}]]")) continue;
            }

            if (text is not null)
            {
                var haystack = string.Concat(
                    new[] { homePath, _paths.Request(sz), _paths.Findings(sz), _paths.Actions(sz) }
                        .Where(File.Exists).Select(File.ReadAllText));
                if (haystack.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }

            results.Add(new KbSearchResult(sz, fm.GetScalar("заказ") ?? "",
                fm.GetList("дефект"), fm.GetList("заменено")));
        }

        return results.OrderBy(r => r.Sz, StringComparer.Ordinal).ToList();
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter KbSearcherTests`
Expected: PASS (4 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Kb/KbSearchResult.cs src/SzDiag.Kb/KbSearcher.cs tests/SzDiag.Kb.Tests/KbSearcherTests.cs
git commit -m "feat(kb): KbSearcher — поиск по заказу и свободному тексту"
```

---

### Task 6: CLI-команды kb record / kb search

**Files:**
- Modify: `src/SzDiag.Cli/CliOptions.cs` (добавить `KbRoot`), `src/SzDiag.Cli/appsettings.json`, `src/SzDiag.Cli/Program.cs`, `src/SzDiag.Cli/SzDiag.Cli.csproj` (ссылка на SzDiag.Kb)

- [ ] **Step 1: Добавить ссылку на SzDiag.Kb**

Run:
```bash
dotnet add src/SzDiag.Cli reference src/SzDiag.Kb
```

- [ ] **Step 2: Добавить KbRoot в опции и appsettings**

В `src/SzDiag.Cli/CliOptions.cs` добавить свойство в класс `CliOptions`:
```csharp
    public string KbRoot { get; set; } = "kb";
```

В `src/SzDiag.Cli/appsettings.json` добавить ключ `"KbRoot": "kb"`:
```json
{
  "HubBaseUrl": "http://localhost:5000",
  "ManagementToken": "dev-token",
  "KbRoot": "kb"
}
```

- [ ] **Step 3: Добавить парсер флагов и команды kb в Program.cs**

В `src/SzDiag.Cli/Program.cs` добавить `using SzDiag.Kb;` в начало и ветку `kb` в `switch`
(перед `default:`):
```csharp
    case "kb" when args.Length >= 2:
        await KbCommand.RunAsync(args[1..], options.KbRoot);
        break;
```

Создать `src/SzDiag.Cli/KbCommand.cs`:
```csharp
using SzDiag.Kb;

namespace SzDiag.Cli;

/// <summary>Разбор подкоманд `kb record` и `kb search`.</summary>
public static class KbCommand
{
    public static Task RunAsync(string[] args, string kbRoot)
    {
        var paths = new KbPaths(kbRoot);
        var sub = args[0].ToLowerInvariant();

        if (sub == "record" && args.Length >= 2)
        {
            var flags = ParseFlags(args[2..]);
            var req = new RecordRequest
            {
                Sz = args[1],
                Order = Single(flags, "order"),
                Device = Single(flags, "device"),
                Defects = Many(flags, "defect"),
                Replaced = Many(flags, "replaced"),
                Findings = Many(flags, "finding"),
                Actions = Many(flags, "action"),
            };
            var scaffolder = new KnowledgeBaseScaffolder(kbRoot);
            new KbRecorder(paths, scaffolder, new EntityNoteWriter(paths)).Record(req);
            Console.WriteLine($"СЗ {req.Sz}: записано в базу знаний.");
            return Task.CompletedTask;
        }

        if (sub == "search")
        {
            var flags = ParseFlags(args[1..]);
            var results = new KbSearcher(paths).Search(Single(flags, "order"), Single(flags, "text"));
            if (results.Count == 0) { Console.WriteLine("Ничего не найдено."); return Task.CompletedTask; }
            foreach (var r in results)
                Console.WriteLine($"  {r.Sz}  заказ={Clean(r.Order)}  дефект={string.Join(",", r.Defects.Select(Clean))}  заменено={string.Join(",", r.Replaced.Select(Clean))}");
            return Task.CompletedTask;
        }

        Console.WriteLine("""
            Использование:
              szcli kb record <СЗ> [--order X] [--device X] [--defect X]... [--replaced X]... [--finding "..."]... [--action "..."]...
              szcli kb search [--order X] [--text "..."]
            """);
        return Task.CompletedTask;
    }

    private static string Clean(string raw) => raw.Trim('"').Replace("[[", "").Replace("]]", "");

    private static Dictionary<string, List<string>> ParseFlags(string[] args)
    {
        var map = new Dictionary<string, List<string>>();
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..].ToLowerInvariant();
            if (!map.TryGetValue(key, out var list)) { list = new List<string>(); map[key] = list; }
            list.Add(args[i + 1]);
        }
        return map;
    }

    private static string? Single(Dictionary<string, List<string>> flags, string key)
        => flags.TryGetValue(key, out var v) ? v[^1] : null;

    private static IReadOnlyList<string> Many(Dictionary<string, List<string>> flags, string key)
        => flags.TryGetValue(key, out var v) ? v : Array.Empty<string>();
}
```

- [ ] **Step 4: Собрать и прогнать все тесты**

Run: `dotnet build`
Expected: Build succeeded.
Run: `dotnet test`
Expected: PASS — все тесты (hub + cli + agent + kb).

- [ ] **Step 5: Smoke — запись и поиск против temp-vault**

Run (Git Bash):
```bash
KB=$(mktemp -d)
SZDIAG_KbRoot="$KB" dotnet run --project src/SzDiag.Cli -- kb record 156864 --order A-1 --defect "Не стартует POST" --replaced "SSD" --finding "нет видеосигнала"
SZDIAG_KbRoot="$KB" dotnet run --project src/SzDiag.Cli -- kb search --order A-1
SZDIAG_KbRoot="$KB" dotnet run --project src/SzDiag.Cli -- kb search --text "видеосигнал"
cat "$KB/СЗ/156864/156864.md"
rm -rf "$KB"
```
Expected: `search --order A-1` печатает строку с `156864`; `search --text` находит его; home-заметка содержит `заказ: "[[A-1]]"` и `дефект: ["[[Не стартует POST]]"]`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(cli): команды kb record/search над SzDiag.Kb"
```

---

## Self-Review (выполнено при написании плана)

**Покрытие спеки:**
- Библиотека `SzDiag.Kb` + перенос scaffolder → Task 1. ✓
- `FrontmatterEditor` (правка frontmatter, сохранение тела/неизвестных ключей) → Task 2. ✓
- Автосоздание связуемых заметок + Dataview-шаблоны + MOC → Task 3. ✓
- `kb record` merge (frontmatter перезапись/дополнение, проза с датой, идемпотентность) → Task 4. ✓
- `kb search` по заказу и тексту, комбинация, пустой результат → Task 5. ✓
- CLI-команды + `KbRoot` в конфиге → Task 6. ✓
- Регресс hub после переноса scaffolder → Task 1 Step 7. ✓

**Плейсхолдеры:** отсутствуют; весь код приведён.

**Согласованность типов:** `KbPaths`, `FrontmatterEditor` (SetScalar/AddToList/GetScalar/GetList/Serialize), `EntityNoteWriter` (EnsureOrder/EnsureDefect/EnsureComponent/EnsureDevice/EnsureMoc), `RecordRequest`, `KbRecorder.Record`, `KbSearchResult`, `KbSearcher.Search(order,text)` — единые сигнатуры между задачами и тестами. Формат токенов frontmatter (`"[[X]]"`) одинаков в recorder и editor-тестах.

**Замечание по smoke (Task 6 Step 5):** переменная окружения CLI-конфига — префикс `SZDIAG_` (как в Фазе 1), ключ `SZDIAG_KbRoot`.
