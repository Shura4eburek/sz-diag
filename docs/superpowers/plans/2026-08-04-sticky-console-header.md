# Липкая панель статуса в консоли — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Верхние строки консоли hub и агента не скроллятся и раз в секунду показывают живой статус сервиса; логи идут потоком под ними.

**Architecture:** ANSI scroll region (DECSTBM) ограничивает область прокрутки нижней частью окна — верхние строки остаются нетронутыми любым `Console.WriteLine`, поэтому перехват логов не нужен. Новый проект `SzDiag.ConsoleUi` содержит всю механику (детект возможностей, установка региона, таймер перерисовки, лок на запись в консоль); hub и агент только поставляют строки статуса. Любая запись в консоль и перерисовка панели идут под одним локом, иначе таймер уводит курсор посреди чужой строки.

**Tech Stack:** C# / net8.0, Spectre.Console 0.57.1, xunit 2.5.3, P/Invoke `kernel32` (`GetConsoleMode`/`SetConsoleMode`).

**Спека:** `docs/superpowers/specs/2026-08-04-sticky-console-header-design.md`

---

## Структура файлов

**Новый проект `src/SzDiag.ConsoleUi`** (библиотека, ссылается только на Spectre.Console):

| Файл | Ответственность |
|---|---|
| `StickyCapabilities.cs` | Чистая функция: можно ли включать липкий режим (перенаправление, VT, высота, конфиг) |
| `Elapsed.cs` | Форматирование длительности («5ч 12м»), переезжает из `SessionTableRenderer` |
| `SyncedConsoleWriter.cs` | `TextWriter`-обёртка, сериализующая запись в консоль общим локом |
| `ITerminalSurface.cs` | Абстракция терминала (ширина/высота/запись) — нужна, чтобы `StickyHeader` был тестируем без реальной консоли |
| `SystemTerminalSurface.cs` | Реализация поверх `Console` + P/Invoke включения VT |
| `Ansi.cs` | Escape-последовательности и `MarkupToAnsi` (Spectre-разметка → ANSI-строка) |
| `MarkupText.cs` | Видимая длина и обрезка строк с разметкой (с учётом экранированных `[[`/`]]`) |
| `StickyHeader.cs` | Жизненный цикл: старт, резерв строк, регион, таймер, ресайз, `Dispose` со сбросом |

**Поставщики строк** (в своих проектах, про VT не знают):

| Файл | Ответственность |
|---|---|
| `src/SzDiag.Hub/HubStatusLine.cs` | Строки статуса хаба из `SessionRegistry`/`HubOptions` + поиск LAN-IP |
| `src/SzDiag.Agent/AgentStatusLine.cs` | Строки статуса агента из СЗ/hub-url/порта/watchdog/uptime |

**Модифицируются:** `SzDiag.sln`, `src/SzDiag.Cli/SessionTableRenderer.cs` (+ его тесты), `src/SzDiag.Cli/SzDiag.Cli.csproj`, `src/SzDiag.Hub/Program.cs`, `src/SzDiag.Hub/HubOptions.cs`, `src/SzDiag.Agent/Program.cs`, `src/SzDiag.Agent/AgentOptions.cs`, `src/SzDiag.Agent/AgentCommandWiring.cs`, `docs/TESTING.md`, `CLAUDE.md`.

**Порядок задач:** 1–5 собирают библиотеку снизу вверх (каждая задача — рабочий, оттестированный кусок). 6–7 включают панель у хаба (первый видимый результат). 8–9 — у агента. 10 — документация.

---

### Task 1: Проект SzDiag.ConsoleUi + решение о фоллбэке

Фоллбэк — единственная логика, от которой зависит, включится режим вообще или нет. Делаем её чистой функцией, чтобы протестировать таблицей случаев без консоли.

**Files:**
- Create: `src/SzDiag.ConsoleUi/SzDiag.ConsoleUi.csproj`
- Create: `src/SzDiag.ConsoleUi/StickyCapabilities.cs`
- Create: `tests/SzDiag.ConsoleUi.Tests/SzDiag.ConsoleUi.Tests.csproj`
- Create: `tests/SzDiag.ConsoleUi.Tests/StickyCapabilitiesTests.cs`
- Modify: `SzDiag.sln`

- [ ] **Step 1: Создать проекты и добавить в солюшен**

```powershell
dotnet new classlib -n SzDiag.ConsoleUi -o src/SzDiag.ConsoleUi -f net8.0
dotnet new xunit -n SzDiag.ConsoleUi.Tests -o tests/SzDiag.ConsoleUi.Tests -f net8.0
dotnet sln add src/SzDiag.ConsoleUi/SzDiag.ConsoleUi.csproj
dotnet sln add tests/SzDiag.ConsoleUi.Tests/SzDiag.ConsoleUi.Tests.csproj
dotnet add tests/SzDiag.ConsoleUi.Tests reference src/SzDiag.ConsoleUi
dotnet add src/SzDiag.ConsoleUi package Spectre.Console --version 0.57.1
```

Удалить сгенерированные болванки `src/SzDiag.ConsoleUi/Class1.cs` и `tests/SzDiag.ConsoleUi.Tests/UnitTest1.cs`.

Привести `src/SzDiag.ConsoleUi/SzDiag.ConsoleUi.csproj` к виду остальных библиотек проекта:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Spectre.Console" Version="0.57.1" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

Тестовый проект должен содержать `<Using Include="Xunit" />` в `ItemGroup`, как в `tests/SzDiag.Cli.Tests/SzDiag.Cli.Tests.csproj`.

**Важно:** все создаваемые `.cs`-файлы сохранять в **UTF-8 с BOM** (в них кириллица; см. CLAUDE.md — иначе PowerShell 5.1 ломает вывод).

- [ ] **Step 2: Написать падающий тест**

Создать `tests/SzDiag.ConsoleUi.Tests/StickyCapabilitiesTests.cs`:

```csharp
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class StickyCapabilitiesTests
{
    [Fact]
    public void Evaluate_AllGood_ReturnsTrue()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: true,
            windowHeight: 30, configEnabled: true);
        Assert.True(r.Enabled);
    }

    [Fact]
    public void Evaluate_OutputRedirected_Disabled()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: true, vtEnabled: true,
            windowHeight: 30, configEnabled: true);
        Assert.False(r.Enabled);
        Assert.Contains("перенаправлен", r.Reason);
    }

    [Fact]
    public void Evaluate_NoVt_Disabled()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: false,
            windowHeight: 30, configEnabled: true);
        Assert.False(r.Enabled);
        Assert.Contains("VT", r.Reason);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(50, true)]
    public void Evaluate_HeightThresholdIsTen(int height, bool expected)
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: true,
            windowHeight: height, configEnabled: true);
        Assert.Equal(expected, r.Enabled);
    }

    [Fact]
    public void Evaluate_ConfigDisabled_Disabled()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: true,
            windowHeight: 30, configEnabled: false);
        Assert.False(r.Enabled);
        Assert.Contains("конфиг", r.Reason);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests`
Expected: FAIL — компиляция не проходит, `StickyCapabilities` не существует.

- [ ] **Step 4: Реализовать**

Создать `src/SzDiag.ConsoleUi/StickyCapabilities.cs`:

```csharp
namespace SzDiag.ConsoleUi;

/// <summary>Результат проверки: можно ли включать липкую панель и почему нет.</summary>
public readonly record struct StickyDecision(bool Enabled, string Reason);

/// <summary>Решение о включении липкого режима. Чистая функция — вся работа с реальной
/// консолью снаружи, чтобы решение можно было проверить таблицей случаев.</summary>
public static class StickyCapabilities
{
    /// <summary>Минимальная высота окна: ниже неё резерв под панель съедает лог целиком.</summary>
    public const int MinWindowHeight = 10;

    public static StickyDecision Evaluate(bool outputRedirected, bool vtEnabled,
        int windowHeight, bool configEnabled)
    {
        if (!configEnabled) return new(false, "выключено в конфиге (ConsoleUi:Sticky)");
        if (outputRedirected) return new(false, "вывод перенаправлен (не консоль)");
        if (!vtEnabled) return new(false, "терминал без поддержки VT");
        if (windowHeight < MinWindowHeight)
            return new(false, $"окно ниже {MinWindowHeight} строк");
        return new(true, "");
    }
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests`
Expected: PASS, 7 тестов (3 факта + 3 случая Theory + 1).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.ConsoleUi tests/SzDiag.ConsoleUi.Tests SzDiag.sln
git commit -m "feat(consoleui): проект ConsoleUi + решение о фоллбэке липкой панели"
```

---

### Task 2: Переезд FormatElapsed в ConsoleUi

Формат длительности нужен и панели, и таблице CLI. Дублировать нельзя — разъедется. Переносим в общий проект, CLI начинает ссылаться.

**Files:**
- Create: `src/SzDiag.ConsoleUi/Elapsed.cs`
- Create: `tests/SzDiag.ConsoleUi.Tests/ElapsedTests.cs`
- Modify: `src/SzDiag.Cli/SessionTableRenderer.cs:64-71`
- Modify: `src/SzDiag.Cli/SzDiag.Cli.csproj`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.ConsoleUi.Tests/ElapsedTests.cs`:

```csharp
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class ElapsedTests
{
    [Fact]
    public void Format_Seconds() =>
        Assert.Equal("44сек", Elapsed.Format(TimeSpan.FromSeconds(44)));

    [Fact]
    public void Format_MinutesAndSeconds() =>
        Assert.Equal("5мин 44сек", Elapsed.Format(TimeSpan.FromSeconds(344)));

    [Fact]
    public void Format_HoursAndMinutes() =>
        Assert.Equal("1ч 05мин", Elapsed.Format(TimeSpan.FromMinutes(65)));

    [Fact]
    public void Format_NegativeClampsToZero() =>
        Assert.Equal("0сек", Elapsed.Format(TimeSpan.FromSeconds(-5)));
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~ElapsedTests`
Expected: FAIL — `Elapsed` не существует.

- [ ] **Step 3: Создать Elapsed**

Создать `src/SzDiag.ConsoleUi/Elapsed.cs` (тело один в один перенесено из `SessionTableRenderer.FormatElapsed`):

```csharp
namespace SzDiag.ConsoleUi;

/// <summary>Человекочитаемая длительность: «44сек» / «5мин 44сек» / «1ч 05мин».</summary>
public static class Elapsed
{
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var total = (int)t.TotalSeconds;
        if (total < 60) return $"{total}сек";
        if (total < 3600) return $"{total / 60}мин {total % 60:D2}сек";
        return $"{total / 3600}ч {(total % 3600) / 60:D2}мин";
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~ElapsedTests`
Expected: PASS, 4 теста.

- [ ] **Step 5: Переключить CLI на общий формат**

В `src/SzDiag.Cli/SzDiag.Cli.csproj` добавить в первый `ItemGroup` с `ProjectReference`:

```xml
    <ProjectReference Include="..\SzDiag.ConsoleUi\SzDiag.ConsoleUi.csproj" />
```

В `src/SzDiag.Cli/SessionTableRenderer.cs` добавить `using SzDiag.ConsoleUi;` к остальным using и **заменить** метод `FormatElapsed` (строки 63-71) на делегирующий — публичным он остаётся, потому что его зовут другие места CLI и его тесты:

```csharp
    /// <summary>Человекочитаемое время. Формат общий с липкой панелью — см. <see cref="Elapsed"/>.</summary>
    public static string FormatElapsed(TimeSpan t) => Elapsed.Format(t);
```

- [ ] **Step 6: Проверить, что ничего не сломалось**

Run: `dotnet build; dotnet test tests/SzDiag.Cli.Tests`
Expected: сборка проходит, все тесты CLI зелёные (поведение `FormatElapsed` не изменилось).

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.ConsoleUi/Elapsed.cs tests/SzDiag.ConsoleUi.Tests/ElapsedTests.cs src/SzDiag.Cli
git commit -m "refactor(cli): формат длительности переехал в ConsoleUi"
```

---

### Task 3: SyncedConsoleWriter

Панель рисуется из таймер-потока, логи пишутся из своих (SignalR, heartbeat, hosted services). Общий лок — единственное, что не даёт курсору уехать посреди строки.

**Files:**
- Create: `src/SzDiag.ConsoleUi/SyncedConsoleWriter.cs`
- Create: `tests/SzDiag.ConsoleUi.Tests/SyncedConsoleWriterTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.ConsoleUi.Tests/SyncedConsoleWriterTests.cs`:

```csharp
using System.Text;
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class SyncedConsoleWriterTests
{
    /// <summary>Writer, который специально «зевает» между символами: без лока
    /// параллельные записи перемешаются, с локом — нет.</summary>
    private sealed class SlowWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            _sb.Append(value);
            Thread.Sleep(1);
        }
        public override string ToString() => _sb.ToString();
    }

    [Fact]
    public void ConcurrentWrites_AreNotInterleaved()
    {
        var inner = new SlowWriter();
        var gate = new object();
        var writer = new SyncedConsoleWriter(inner, gate);

        Parallel.For(0, 8, i => writer.Write(i % 2 == 0 ? "AAAA" : "BBBB"));

        var text = inner.ToString();
        Assert.Equal(32, text.Length);
        // Каждая четвёрка символов должна быть однородной — иначе записи перемешались.
        for (var i = 0; i < text.Length; i += 4)
            Assert.Equal(1, text.Substring(i, 4).Distinct().Count());
    }

    [Fact]
    public void WriteLine_GoesThroughToInner()
    {
        var inner = new StringWriter();
        var writer = new SyncedConsoleWriter(inner, new object());
        writer.WriteLine("привет");
        Assert.Contains("привет", inner.ToString());
    }

    [Fact]
    public void RunLocked_UsesSameGate_BlocksWriters()
    {
        var inner = new SlowWriter();
        var gate = new object();
        var writer = new SyncedConsoleWriter(inner, gate);
        var insideLock = false;
        var sawWriteDuringLock = false;

        var t = Task.Run(() =>
        {
            lock (gate)
            {
                insideLock = true;
                Thread.Sleep(50);
                if (inner.ToString().Length > 0) sawWriteDuringLock = true;
                insideLock = false;
            }
        });

        while (!insideLock && !t.IsCompleted) Thread.Sleep(1);
        writer.Write("XXXX");
        t.Wait();

        Assert.False(sawWriteDuringLock);
        Assert.Equal("XXXX", inner.ToString());
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~SyncedConsoleWriter`
Expected: FAIL — `SyncedConsoleWriter` не существует.

- [ ] **Step 3: Реализовать**

Создать `src/SzDiag.ConsoleUi/SyncedConsoleWriter.cs`:

```csharp
using System.Text;

namespace SzDiag.ConsoleUi;

/// <summary>
/// Обёртка над консольным writer'ом, сериализующая записи общим локом.
/// Тем же локом пользуется <see cref="StickyHeader"/> при перерисовке панели: иначе
/// таймер переставит курсор наверх посреди чужой строки, и её хвост уедет в панель.
/// </summary>
public sealed class SyncedConsoleWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly object _gate;

    public SyncedConsoleWriter(TextWriter inner, object gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) { lock (_gate) _inner.Write(value); }
    public override void Write(string? value) { lock (_gate) _inner.Write(value); }
    public override void WriteLine() { lock (_gate) _inner.WriteLine(); }
    public override void WriteLine(string? value) { lock (_gate) _inner.WriteLine(value); }
    public override void Flush() { lock (_gate) _inner.Flush(); }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~SyncedConsoleWriter`
Expected: PASS, 3 теста.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.ConsoleUi/SyncedConsoleWriter.cs tests/SzDiag.ConsoleUi.Tests/SyncedConsoleWriterTests.cs
git commit -m "feat(consoleui): SyncedConsoleWriter — общий лок на запись в консоль"
```

---

### Task 4: Escape-последовательности и рендер разметки

Вся ANSI-механика в одном месте, чтобы `StickyHeader` не собирал escape-строки вручную и чтобы их можно было проверить тестом.

**Files:**
- Create: `src/SzDiag.ConsoleUi/Ansi.cs`
- Create: `tests/SzDiag.ConsoleUi.Tests/AnsiTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.ConsoleUi.Tests/AnsiTests.cs`:

```csharp
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class AnsiTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void SetScrollRegion_EmitsDecstbm() =>
        Assert.Equal($"{Esc}[4;30r", Ansi.SetScrollRegion(4, 30));

    [Fact]
    public void ResetScrollRegion_EmitsBareR() =>
        Assert.Equal($"{Esc}[r", Ansi.ResetScrollRegion);

    [Fact]
    public void MoveCursor_IsOneBased() =>
        Assert.Equal($"{Esc}[1;1H", Ansi.MoveCursor(1, 1));

    [Fact]
    public void SaveRestore_UseDecScDecRc()
    {
        // DECSC/DECRC (ESC 7 / ESC 8) надёжнее SCO-варианта в conhost.
        Assert.Equal($"{Esc}7", Ansi.SaveCursor);
        Assert.Equal($"{Esc}8", Ansi.RestoreCursor);
    }

    [Fact]
    public void MarkupToAnsi_RendersColorAndKeepsText()
    {
        var s = Ansi.MarkupToAnsi("[green]online[/] дальше");
        Assert.Contains("online", s);
        Assert.Contains("дальше", s);
        Assert.Contains(Esc, s);           // цвет реально применён
        Assert.DoesNotContain("[green]", s); // разметка съедена, а не напечатана
        Assert.DoesNotContain("\n", s);    // панель рисуется построчно, переводов быть не должно
    }

    [Fact]
    public void MarkupToAnsi_DoesNotWrapLongLines()
    {
        var s = Ansi.MarkupToAnsi(new string('x', 500));
        Assert.DoesNotContain("\n", s);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~AnsiTests`
Expected: FAIL — `Ansi` не существует.

- [ ] **Step 3: Реализовать**

Создать `src/SzDiag.ConsoleUi/Ansi.cs`:

```csharp
using Spectre.Console;

namespace SzDiag.ConsoleUi;

/// <summary>Escape-последовательности VT и рендер Spectre-разметки в готовую ANSI-строку.</summary>
public static class Ansi
{
    private const string Esc = "\u001b";

    /// <summary>DECSTBM: ограничить прокрутку строками [top..bottom] (1-based, включительно).</summary>
    public static string SetScrollRegion(int top, int bottom) => $"{Esc}[{top};{bottom}r";

    /// <summary>Вернуть прокрутку на всё окно. Обязателен при выходе, иначе консоль
    /// остаётся с усечённой областью и после завершения процесса.</summary>
    public const string ResetScrollRegion = Esc + "[r";

    /// <summary>DECSC — сохранить позицию курсора (надёжнее SCO ESC[s в conhost).</summary>
    public const string SaveCursor = Esc + "7";

    /// <summary>DECRC — восстановить позицию курсора.</summary>
    public const string RestoreCursor = Esc + "8";

    /// <summary>CUP: поставить курсор (1-based).</summary>
    public static string MoveCursor(int row, int col) => $"{Esc}[{row};{col}H";

    /// <summary>EL: стереть от курсора до конца строки — чтобы хвост прошлой,
    /// более длинной, версии панели не оставался на экране.</summary>
    public const string ClearToEol = Esc + "[K";

    /// <summary>
    /// Разметка Spectre → ANSI-строка без переводов строки. Ширина профиля задана
    /// заведомо большой: перенос строк недопустим (строка панели должна остаться одной
    /// строкой), за длину отвечает поставщик строк, который получает доступную ширину.
    /// </summary>
    public static string MarkupToAnsi(string markup)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 10_000;
        console.Profile.Height = 10_000;
        console.Markup(markup);
        return writer.ToString().Replace("\r", "").Replace("\n", "");
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~AnsiTests`
Expected: PASS, 6 тестов.

- [ ] **Step 5: Написать падающий тест на работу с разметкой**

Панели надо считать видимую длину строки и резать её по ширине, не ломая теги. Логика
общая для хаба и агента — живёт здесь, а не дублируется в обоих.

**Ловушка:** в Spectre `[[` и `]]` — это экранированные литеральные `[` и `]`. Наивная
регулярка `\[/?[^\]]*\]` на строке `[[C]]` съест `[[C]` и оставит `]`, то есть и длину
посчитает неверно, и текст испортит. Экранированные скобки надо снимать отдельно.

Создать `tests/SzDiag.ConsoleUi.Tests/MarkupTextTests.cs`:

```csharp
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class MarkupTextTests
{
    [Fact]
    public void Plain_StripsTags() =>
        Assert.Equal("online дальше", MarkupText.Plain("[green]online[/] дальше"));

    [Fact]
    public void Plain_UnescapesDoubleBrackets() =>
        Assert.Equal("[C] закрыть", MarkupText.Plain("[green][[C]][/] закрыть"));

    [Fact]
    public void Plain_HandlesBareText() =>
        Assert.Equal("просто текст", MarkupText.Plain("просто текст"));

    [Fact]
    public void PlainLength_CountsVisibleOnly() =>
        Assert.Equal(6, MarkupText.PlainLength("[green]online[/]"));

    [Fact]
    public void PlainLength_CountsEscapedBracketsAsOne() =>
        Assert.Equal(3, MarkupText.PlainLength("[green][[C]][/]"));

    [Fact]
    public void Fit_ShorterThanWidth_ReturnsUnchanged()
    {
        const string s = "[green]online[/]";
        Assert.Equal(s, MarkupText.Fit(s, 20));
    }

    [Fact]
    public void Fit_TrimsVisibleTextToWidth()
    {
        var fitted = MarkupText.Fit("[green]abcdefghij[/]", 4);
        Assert.Equal(4, MarkupText.PlainLength(fitted));
        Assert.Equal("abcd", MarkupText.Plain(fitted));
    }

    [Fact]
    public void Fit_KeepsEscapedBracketsIntact()
    {
        var fitted = MarkupText.Fit("[green][[C]][/] закрыть СЗ", 3);
        Assert.Equal("[C]", MarkupText.Plain(fitted));
    }

    [Fact]
    public void Fit_ZeroWidth_ReturnsEmpty() =>
        Assert.Equal("", MarkupText.Fit("[green]online[/]", 0));
}
```

- [ ] **Step 6: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~MarkupText`
Expected: FAIL — `MarkupText` не существует.

- [ ] **Step 7: Реализовать MarkupText**

Создать `src/SzDiag.ConsoleUi/MarkupText.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace SzDiag.ConsoleUi;

/// <summary>
/// Работа с длиной и обрезкой строк со Spectre-разметкой. Панель рисует строку как есть,
/// без переноса, поэтому поставщики статуса обязаны укладываться в заданную ширину —
/// считать её надо по видимому тексту, а не по длине строки с тегами.
/// </summary>
public static class MarkupText
{
    private static readonly Regex TagPattern = new(@"\[/?[^\]]*\]", RegexOptions.Compiled);

    // Плейсхолдеры под экранированные скобки: снимаем их до разбора тегов, иначе
    // «[[C]]» будет разобрано как тег и превратится в «]».
    private const char OpenPlaceholder = '\u0001';
    private const char ClosePlaceholder = '\u0002';

    /// <summary>Видимый текст без разметки (экранированные скобки развёрнуты).</summary>
    public static string Plain(string markup)
    {
        var masked = markup.Replace("[[", OpenPlaceholder.ToString())
                           .Replace("]]", ClosePlaceholder.ToString());
        var stripped = TagPattern.Replace(masked, "");
        return stripped.Replace(OpenPlaceholder, '[').Replace(ClosePlaceholder, ']');
    }

    /// <summary>Длина видимого текста.</summary>
    public static int PlainLength(string markup) => Plain(markup).Length;

    /// <summary>Режет видимый текст до width, не разрывая теги.</summary>
    public static string Fit(string markup, int width)
    {
        if (width <= 0) return "";
        if (PlainLength(markup) <= width) return markup;

        var result = new StringBuilder();
        var visible = 0;
        var i = 0;
        while (i < markup.Length)
        {
            // Экранированная скобка — один видимый символ, двигаемся на два.
            if (i + 1 < markup.Length &&
                ((markup[i] == '[' && markup[i + 1] == '[') || (markup[i] == ']' && markup[i + 1] == ']')))
            {
                if (visible >= width) break;
                result.Append(markup[i]).Append(markup[i + 1]);
                visible++;
                i += 2;
                continue;
            }

            // Тег — копируем целиком, ширину не тратит.
            if (markup[i] == '[')
            {
                var close = markup.IndexOf(']', i);
                if (close < 0) break;
                result.Append(markup, i, close - i + 1);
                i = close + 1;
                continue;
            }

            if (visible >= width) break;
            result.Append(markup[i]);
            visible++;
            i++;
        }
        return result.ToString();
    }
}
```

- [ ] **Step 8: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~MarkupText`
Expected: PASS, 9 тестов.

- [ ] **Step 9: Коммит**

```bash
git add src/SzDiag.ConsoleUi/Ansi.cs src/SzDiag.ConsoleUi/MarkupText.cs tests/SzDiag.ConsoleUi.Tests/AnsiTests.cs tests/SzDiag.ConsoleUi.Tests/MarkupTextTests.cs
git commit -m "feat(consoleui): escape-последовательности VT, рендер разметки и обрезка по ширине"
```

---

### Task 5: StickyHeader

Ядро: резерв строк, регион, перерисовка, отслеживание ресайза, сброс на выходе. Тестируется через `ITerminalSurface`, реальная консоль в тестах не участвует.

**Files:**
- Create: `src/SzDiag.ConsoleUi/ITerminalSurface.cs`
- Create: `src/SzDiag.ConsoleUi/SystemTerminalSurface.cs`
- Create: `src/SzDiag.ConsoleUi/StickyHeader.cs`
- Create: `tests/SzDiag.ConsoleUi.Tests/StickyHeaderTests.cs`

- [ ] **Step 1: Создать абстракцию терминала**

Создать `src/SzDiag.ConsoleUi/ITerminalSurface.cs`:

```csharp
namespace SzDiag.ConsoleUi;

/// <summary>Терминал глазами панели: размеры и запись сырой ANSI-строки.
/// Существует ради тестируемости — реальная реализация одна.</summary>
public interface ITerminalSurface
{
    int Width { get; }
    int Height { get; }
    bool OutputRedirected { get; }
    /// <summary>Пишет как есть, без перевода строки.</summary>
    void Write(string raw);
}
```

Создать `src/SzDiag.ConsoleUi/SystemTerminalSurface.cs`:

```csharp
using System.Runtime.InteropServices;

namespace SzDiag.ConsoleUi;

/// <summary>Реальная консоль Windows. Пишет через переданный writer — тот же
/// (залоченный) поток, что и весь остальной вывод процесса.</summary>
public sealed class SystemTerminalSurface : ITerminalSurface
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly TextWriter _out;

    public SystemTerminalSurface(TextWriter output) => _out = output;

    /// <summary>Включает обработку escape-последовательностей. false — старый conhost
    /// без VT: escape-коды в нём напечатались бы как мусор, липкий режим не включаем.</summary>
    public static bool TryEnableVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
    }

    public int Width { get { try { return Console.WindowWidth; } catch { return 0; } } }
    public int Height { get { try { return Console.WindowHeight; } catch { return 0; } } }
    public bool OutputRedirected { get { try { return Console.IsOutputRedirected; } catch { return true; } } }

    public void Write(string raw) => _out.Write(raw);
}
```

- [ ] **Step 2: Написать падающий тест**

Создать `tests/SzDiag.ConsoleUi.Tests/StickyHeaderTests.cs`:

```csharp
using System.Text;
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class StickyHeaderTests
{
    private const string Esc = "\u001b";

    private sealed class FakeSurface : ITerminalSurface
    {
        private readonly StringBuilder _sb = new();
        public int Width { get; set; } = 100;
        public int Height { get; set; } = 30;
        public bool OutputRedirected { get; set; }
        public void Write(string raw) { lock (_sb) _sb.Append(raw); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
        public void Clear() { lock (_sb) _sb.Clear(); }
    }

    private static StickyHeader? Start(FakeSurface surface, Func<int, IReadOnlyList<string>> render,
        bool configEnabled = true, bool vt = true, int lines = 2) =>
        StickyHeader.TryStart(render, new StickyOptions(Lines: lines, ConfigEnabled: configEnabled),
            surface, vtEnabled: vt, gate: new object(), autoRefresh: false);

    [Fact]
    public void TryStart_SetsScrollRegionBelowPanel()
    {
        var s = new FakeSurface { Height = 30 };
        using var h = Start(s, _ => new[] { "первая", "вторая" });

        Assert.NotNull(h);
        // 2 строки текста + разделитель = 3 зарезервированных, прокрутка с 4-й по 30-ю.
        Assert.Contains($"{Esc}[4;30r", s.Text);
    }

    [Fact]
    public void TryStart_WhenRedirected_ReturnsNull()
    {
        var s = new FakeSurface { OutputRedirected = true };
        var h = Start(s, _ => new[] { "a", "b" });
        Assert.Null(h);
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void TryStart_WhenNoVt_ReturnsNull()
    {
        var s = new FakeSurface();
        Assert.Null(Start(s, _ => new[] { "a", "b" }, vt: false));
    }

    [Fact]
    public void TryStart_WhenConfigDisabled_ReturnsNull()
    {
        var s = new FakeSurface();
        Assert.Null(Start(s, _ => new[] { "a", "b" }, configEnabled: false));
    }

    [Fact]
    public void TryStart_WhenWindowTooShort_ReturnsNull()
    {
        var s = new FakeSurface { Height = 9 };
        Assert.Null(Start(s, _ => new[] { "a", "b" }));
    }

    [Fact]
    public void Refresh_DrawsTextAndRestoresCursor()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "СЗ 156864", "хоткеи" });
        s.Clear();

        h!.Refresh();

        var text = s.Text;
        Assert.StartsWith(Ansi.SaveCursor, text);
        Assert.EndsWith(Ansi.RestoreCursor, text);
        Assert.Contains("СЗ 156864", text);
        Assert.Contains("хоткеи", text);
        Assert.Contains(Ansi.ClearToEol, text);
    }

    [Fact]
    public void Refresh_PassesAvailableWidthToRenderer()
    {
        var s = new FakeSurface { Width = 77 };
        var seen = 0;
        using var h = Start(s, w => { seen = w; return new[] { "a", "b" }; });
        h!.Refresh();
        Assert.Equal(77, seen);
    }

    [Fact]
    public void Refresh_PadsMissingLines_SoRegionStaysStable()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "одна" });  // поставщик вернул меньше, чем Lines=2
        s.Clear();
        h!.Refresh();
        // Обе строки панели должны быть отрисованы (вторая — пустой с очисткой хвоста).
        Assert.Contains(Ansi.MoveCursor(1, 1), s.Text);
        Assert.Contains(Ansi.MoveCursor(2, 1), s.Text);
    }

    [Fact]
    public void Refresh_TrimsExtraLines_SoRegionStaysStable()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => new[] { "a", "b", "c", "d" });
        s.Clear();
        h!.Refresh();
        Assert.DoesNotContain(Ansi.MoveCursor(3, 1), s.Text);
    }

    [Fact]
    public void Refresh_AfterResize_ReestablishesRegion()
    {
        var s = new FakeSurface { Height = 30 };
        using var h = Start(s, _ => new[] { "a", "b" });
        s.Clear();

        s.Height = 50;
        h!.Refresh();

        Assert.Contains($"{Esc}[4;50r", s.Text);
    }

    [Fact]
    public void Refresh_WhenWindowShrinksBelowThreshold_DisablesItself()
    {
        var s = new FakeSurface { Height = 30 };
        using var h = Start(s, _ => new[] { "a", "b" });
        s.Clear();

        s.Height = 5;
        h!.Refresh();
        Assert.Contains(Ansi.ResetScrollRegion, s.Text);

        s.Clear();
        h.Refresh();
        Assert.Equal("", s.Text);   // режим выключен насовсем, больше ничего не пишем
    }

    [Fact]
    public void Dispose_ResetsScrollRegion()
    {
        var s = new FakeSurface();
        var h = Start(s, _ => new[] { "a", "b" });
        s.Clear();
        h!.Dispose();
        Assert.Contains(Ansi.ResetScrollRegion, s.Text);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var s = new FakeSurface();
        var h = Start(s, _ => new[] { "a", "b" });
        h!.Dispose();
        s.Clear();
        h.Dispose();
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void Refresh_WhenRendererThrows_DoesNotPropagate()
    {
        var s = new FakeSurface();
        using var h = Start(s, _ => throw new InvalidOperationException("реестр моргнул"));
        var ex = Record.Exception(() => h!.Refresh());
        Assert.Null(ex);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests --filter FullyQualifiedName~StickyHeaderTests`
Expected: FAIL — `StickyHeader`/`StickyOptions` не существуют.

- [ ] **Step 4: Реализовать**

Создать `src/SzDiag.ConsoleUi/StickyHeader.cs`:

```csharp
namespace SzDiag.ConsoleUi;

/// <summary>Настройки липкой панели.</summary>
/// <param name="Lines">Сколько строк текста в панели. Фиксировано, чтобы резерв под
/// панель не «дышал»: поставщик, вернувший больше или меньше, обрезается/добивается.</param>
/// <param name="ConfigEnabled">Рубильник из конфига (ConsoleUi:Sticky).</param>
/// <param name="RefreshInterval">Период автоперерисовки.</param>
public sealed record StickyOptions(
    int Lines = 2,
    bool ConfigEnabled = true,
    TimeSpan? RefreshInterval = null);

/// <summary>
/// Липкая панель в верхних строках консоли. Работает через ANSI scroll region (DECSTBM):
/// область прокрутки сдвигается ниже панели, поэтому обычный вывод (логи ASP.NET,
/// Announce агента, вывод дочерних процессов) скроллит только нижнюю часть окна,
/// а панель остаётся на месте. Перехватывать логи не требуется.
/// </summary>
public sealed class StickyHeader : IDisposable
{
    private readonly Func<int, IReadOnlyList<string>> _render;
    private readonly ITerminalSurface _surface;
    private readonly object _gate;
    private readonly int _lines;
    private readonly int _reserved;      // строки текста + разделитель
    private readonly Timer? _timer;

    private int _knownHeight;
    private int _knownWidth;
    private bool _active;
    private bool _disposed;

    private StickyHeader(Func<int, IReadOnlyList<string>> render, ITerminalSurface surface,
        object gate, StickyOptions options, bool autoRefresh)
    {
        _render = render;
        _surface = surface;
        _gate = gate;
        _lines = options.Lines;
        _reserved = options.Lines + 1;   // +1 — разделительная линия под панелью
        _knownHeight = surface.Height;
        _knownWidth = surface.Width;
        _active = true;

        SetupRegion();
        Refresh();

        if (autoRefresh)
        {
            var period = options.RefreshInterval ?? TimeSpan.FromSeconds(1);
            _timer = new Timer(_ => Refresh(), null, period, period);
        }
    }

    /// <summary>
    /// Пытается включить липкий режим. Возвращает null, если условия не выполнены
    /// (перенаправленный вывод, нет VT, низкое окно, выключено конфигом) — вызывающий
    /// в этом случае просто работает как раньше, линейным выводом.
    /// </summary>
    /// <param name="render">Получает доступную ширину, возвращает строки со Spectre-разметкой.</param>
    /// <param name="gate">Тот же лок, что у <see cref="SyncedConsoleWriter"/>.</param>
    public static StickyHeader? TryStart(
        Func<int, IReadOnlyList<string>> render,
        StickyOptions options,
        ITerminalSurface surface,
        bool vtEnabled,
        object gate,
        bool autoRefresh = true)
    {
        var decision = StickyCapabilities.Evaluate(
            surface.OutputRedirected, vtEnabled, surface.Height, options.ConfigEnabled);
        if (!decision.Enabled) return null;

        return new StickyHeader(render, surface, gate, options, autoRefresh);
    }

    /// <summary>Резервирует место под панель и сдвигает область прокрутки вниз.</summary>
    private void SetupRegion()
    {
        lock (_gate)
        {
            // Пустые строки — чтобы панель не легла поверх уже напечатанного текста.
            for (var i = 0; i < _reserved; i++) _surface.Write("\n");
            _surface.Write(Ansi.SetScrollRegion(_reserved + 1, _knownHeight));
            // Курсор — в начало области прокрутки, иначе первый лог уйдёт под панель.
            _surface.Write(Ansi.MoveCursor(_reserved + 1, 1));
        }
    }

    /// <summary>Перерисовывает панель. Безопасно звать из любого потока.</summary>
    public void Refresh()
    {
        if (_disposed || !_active) return;

        // Ресайз: событий на Windows нет, поэтому сверяем размеры на каждом тике.
        var height = _surface.Height;
        var width = _surface.Width;
        if (height != _knownHeight || width != _knownWidth)
        {
            if (height < StickyCapabilities.MinWindowHeight)
            {
                // Окно ужали до неприличия — выключаемся насовсем, дальше линейный вывод.
                lock (_gate)
                {
                    _surface.Write(Ansi.ResetScrollRegion);
                    _active = false;
                }
                return;
            }
            _knownHeight = height;
            _knownWidth = width;
            lock (_gate) _surface.Write(Ansi.SetScrollRegion(_reserved + 1, _knownHeight));
        }

        IReadOnlyList<string> lines;
        try { lines = _render(_knownWidth); }
        catch { return; }   // упавший поставщик статуса не должен ронять процесс

        lock (_gate)
        {
            if (!_active) return;
            _surface.Write(Ansi.SaveCursor);
            for (var i = 0; i < _lines; i++)
            {
                var markup = i < lines.Count ? lines[i] : "";
                _surface.Write(Ansi.MoveCursor(i + 1, 1));
                _surface.Write(Ansi.ClearToEol);
                _surface.Write(Ansi.MarkupToAnsi(markup));
            }
            _surface.Write(Ansi.MoveCursor(_lines + 1, 1));
            _surface.Write(Ansi.ClearToEol);
            _surface.Write(Ansi.MarkupToAnsi($"[grey]{new string('─', Math.Max(0, _knownWidth - 1))}[/]"));
            _surface.Write(Ansi.RestoreCursor);
        }
    }

    /// <summary>Сбрасывает область прокрутки. Без этого консоль остаётся усечённой
    /// и после завершения процесса.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            _surface.Write(Ansi.ResetScrollRegion);
        }
    }
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.ConsoleUi.Tests`
Expected: PASS — все тесты проекта, включая 14 тестов `StickyHeaderTests`.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.ConsoleUi tests/SzDiag.ConsoleUi.Tests
git commit -m "feat(consoleui): StickyHeader — липкая панель через ANSI scroll region"
```

---

### Task 6: Строки статуса хаба

**Files:**
- Create: `src/SzDiag.Hub/HubStatusLine.cs`
- Create: `tests/SzDiag.Hub.Tests/HubStatusLineTests.cs`
- Modify: `src/SzDiag.Hub/SzDiag.Hub.csproj`

- [ ] **Step 1: Подключить ConsoleUi к хабу**

В `src/SzDiag.Hub/SzDiag.Hub.csproj` добавить в `ItemGroup` с `ProjectReference`:

```xml
    <ProjectReference Include="..\SzDiag.ConsoleUi\SzDiag.ConsoleUi.csproj" />
```

- [ ] **Step 2: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/HubStatusLineTests.cs`:

```csharp
using SzDiag.Contracts;
using SzDiag.Hub;

namespace SzDiag.Hub.Tests;

public class HubStatusLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(string sz, SessionStatus status = SessionStatus.Online,
        string? activity = null, DateTimeOffset? activitySince = null) =>
        new(sz, "192.168.1.50", "PC-" + sz, status, Now, Now,
            Activity: activity, ActivitySince: activitySince);

    private static HubStatusContext Ctx(IReadOnlyList<SessionInfo> sessions) => new(
        Sessions: sessions,
        ListenUrls: "0.0.0.0:5099",
        LanIp: "192.168.1.10",
        KbRoot: @"C:\Users\ENDI\kb",
        StartedAt: Now - TimeSpan.FromHours(5) - TimeSpan.FromMinutes(12),
        Now: Now);

    /// <summary>Убирает Spectre-разметку — тесты проверяют текст, а не цвета.</summary>
    private static string Plain(string markup) => SzDiag.ConsoleUi.MarkupText.Plain(markup);

    [Fact]
    public void Render_ShowsListenAddressLanIpAndUptime()
    {
        var lines = HubStatusLine.Render(Ctx(Array.Empty<SessionInfo>()), width: 120);
        Assert.Equal(2, lines.Count);
        var first = Plain(lines[0]);
        Assert.Contains("0.0.0.0:5099", first);
        Assert.Contains("192.168.1.10", first);
        Assert.Contains("5ч 12мин", first);
    }

    [Fact]
    public void Render_NoSessions_SaysSo()
    {
        var lines = HubStatusLine.Render(Ctx(Array.Empty<SessionInfo>()), width: 120);
        Assert.Contains("нет активных СЗ", Plain(lines[1]));
    }

    [Fact]
    public void Render_ListsOnlineSessionsWithCount()
    {
        var lines = HubStatusLine.Render(
            Ctx(new[] { Session("156864"), Session("160176") }), width: 120);
        var second = Plain(lines[1]);
        Assert.Contains("онлайн 2:", second);
        Assert.Contains("156864", second);
        Assert.Contains("160176", second);
    }

    [Fact]
    public void Render_SkipsOfflineSessions()
    {
        var lines = HubStatusLine.Render(
            Ctx(new[] { Session("156864"), Session("999999", SessionStatus.Offline) }), width: 120);
        var second = Plain(lines[1]);
        Assert.Contains("онлайн 1:", second);
        Assert.DoesNotContain("999999", second);
    }

    [Fact]
    public void Render_ShowsActivityWithElapsed()
    {
        var lines = HubStatusLine.Render(
            Ctx(new[] { Session("156864", activity: "OCCT", activitySince: Now - TimeSpan.FromMinutes(42)) }),
            width: 120);
        Assert.Contains("156864 (OCCT 42мин 00сек)", Plain(lines[1]));
    }

    [Fact]
    public void Render_TruncatesSessionListToWidth_WithPlusN()
    {
        var many = Enumerable.Range(0, 12).Select(i => Session($"16000{i}")).ToArray();
        var lines = HubStatusLine.Render(Ctx(many), width: 60);
        var second = Plain(lines[1]);
        Assert.True(second.Length <= 60, $"строка длиннее ширины: {second.Length}");
        Assert.Contains("+", second);
    }

    [Fact]
    public void Render_DropsKbTail_WhenNarrow()
    {
        var wide = Plain(HubStatusLine.Render(Ctx(Array.Empty<SessionInfo>()), width: 200)[1]);
        var narrow = Plain(HubStatusLine.Render(Ctx(Array.Empty<SessionInfo>()), width: 50)[1]);
        Assert.Contains("kb", wide);
        Assert.DoesNotContain("kb", narrow);
    }

    [Fact]
    public void Render_NeverExceedsWidth()
    {
        var many = Enumerable.Range(0, 30).Select(i => Session($"1600{i:D2}",
            activity: "OCCT", activitySince: Now - TimeSpan.FromMinutes(5))).ToArray();
        foreach (var width in new[] { 40, 60, 80, 120, 200 })
        {
            var lines = HubStatusLine.Render(Ctx(many), width);
            foreach (var line in lines)
                Assert.True(Plain(line).Length <= width,
                    $"ширина {width}: строка длиной {Plain(line).Length}");
        }
    }

    [Fact]
    public void Render_VeryNarrow_DoesNotThrow()
    {
        var ex = Record.Exception(() => HubStatusLine.Render(Ctx(new[] { Session("156864") }), width: 5));
        Assert.Null(ex);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~HubStatusLine`
Expected: FAIL — `HubStatusLine`/`HubStatusContext` не существуют.

- [ ] **Step 4: Реализовать**

Создать `src/SzDiag.Hub/HubStatusLine.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Spectre.Console;
using SzDiag.ConsoleUi;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Всё, что нужно панели хаба. Собирается на каждом тике перерисовки.</summary>
public sealed record HubStatusContext(
    IReadOnlyList<SessionInfo> Sessions,
    string ListenUrls,
    string LanIp,
    string KbRoot,
    DateTimeOffset StartedAt,
    DateTimeOffset Now);

/// <summary>Две строки статуса хаба для липкой панели. Обязана уложиться в заданную
/// ширину: панель рисует строку как есть, перенос недопустим.</summary>
public static class HubStatusLine
{
    public static IReadOnlyList<string> Render(HubStatusContext ctx, int width)
    {
        var uptime = Elapsed.Format(ctx.Now - ctx.StartedAt);
        var first = $"[bold]sz-diag hub[/]  [green]●[/] слушает {Markup.Escape(ctx.ListenUrls)}" +
                    $"   [grey]LAN[/] {Markup.Escape(ctx.LanIp)}   [grey]аптайм[/] {uptime}";
        first = Fit(first, width);

        var online = ctx.Sessions.Where(s => s.Status == SessionStatus.Online)
            .OrderBy(s => s.Sz).ToList();

        string second;
        if (online.Count == 0)
        {
            second = "[dim]нет активных СЗ[/]";
        }
        else
        {
            var kbTail = $"   [grey]kb[/] {Markup.Escape(ctx.KbRoot)}";
            var budget = width - PlainLength(kbTail);
            var list = SessionList(online, ctx.Now, budget);
            // kb-хвост влезает только если после списка осталось место — иначе отбрасываем.
            second = PlainLength(list) + PlainLength(kbTail) <= width ? list + kbTail : list;
        }

        return new[] { first, Fit(second, width) };
    }

    /// <summary>«онлайн 3: 156864 (OCCT 42мин 00сек), 160176, 161288» с обрезкой по бюджету.</summary>
    private static string SessionList(IReadOnlyList<SessionInfo> online, DateTimeOffset now, int budget)
    {
        var prefix = $"[grey]онлайн[/] {online.Count}: ";
        var used = PlainLength(prefix);
        var parts = new List<string>();
        var shown = 0;

        foreach (var s in online)
        {
            var cell = Markup.Escape(s.Sz);
            if (!string.IsNullOrEmpty(s.Activity) && s.ActivitySince is { } since)
                cell += $" [yellow]({Markup.Escape(s.Activity)} {Elapsed.Format(now - since)})[/]";
            else if (!string.IsNullOrEmpty(s.Activity))
                cell += $" [grey]({Markup.Escape(s.Activity)})[/]";

            var addition = (shown == 0 ? 0 : 2) + PlainLength(cell);   // 2 — «, »
            var rest = online.Count - shown - 1;
            var tail = rest > 0 ? $" +{rest}".Length : 0;
            if (used + addition + tail > budget && shown > 0) break;

            parts.Add(cell);
            used += addition;
            shown++;
        }

        var text = prefix + string.Join(", ", parts);
        if (shown < online.Count) text += $" [dim]+{online.Count - shown}[/]";
        return text;
    }

    private static int PlainLength(string markup) => MarkupText.PlainLength(markup);

    /// <summary>Страховка от переполнения. Срабатывать не должна — считается ошибкой
    /// сборки строки выше, но лучше обрезать, чем сломать панель.</summary>
    private static string Fit(string markup, int width) => MarkupText.Fit(markup, width);

    /// <summary>IPv4 первого рабочего не-loopback интерфейса — то, что писать в панель как
    /// адрес, по которому агенты видят hub. Определяется один раз при старте.</summary>
    public static string FindLanIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    return addr.Address.ToString();
                }
            }
        }
        catch { /* не критично — покажем прочерк */ }
        return "—";
    }
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~HubStatusLine`
Expected: PASS, 9 тестов.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hub/HubStatusLine.cs src/SzDiag.Hub/SzDiag.Hub.csproj tests/SzDiag.Hub.Tests/HubStatusLineTests.cs
git commit -m "feat(hub): строки статуса для липкой панели"
```

---

### Task 7: Включить панель в хабе

**Files:**
- Modify: `src/SzDiag.Hub/HubOptions.cs`
- Modify: `src/SzDiag.Hub/Program.cs:1-7`, `:50-67`, `:85-89`

- [ ] **Step 1: Добавить настройку в HubOptions**

В `src/SzDiag.Hub/HubOptions.cs` добавить в конец класса:

```csharp
    /// <summary>Липкая панель статуса в верхних строках консоли. false — обычный
    /// линейный вывод (рубильник на случай проблемного терминала).</summary>
    public bool StickyHeader { get; set; } = true;
```

Читается как `Hub:StickyHeader` — секция `Hub` уже биндится в `Program.cs:18`.

- [ ] **Step 2: Подключить панель в Program.cs**

В `src/SzDiag.Hub/Program.cs` добавить к using'ам:

```csharp
using SzDiag.ConsoleUi;
```

Сразу после строки `var builder = WebApplication.CreateBuilder(args);` (строка 7) вставить — **до** `builder.Build()`, чтобы консольный логгер получил уже залоченный writer:

```csharp
// Единый лок на запись в консоль: липкая панель перерисовывается из таймер-потока,
// логи пишутся из своих. Без лока курсор уедет посреди чужой строки.
var consoleGate = new object();
Console.SetOut(new SyncedConsoleWriter(Console.Out, consoleGate));
```

**Заменить** блок `app.Lifetime.ApplicationStarted.Register(...)` (строки 56-67) на:

```csharp
// Липкая панель со сводкой и живым списком онлайн-СЗ. Если терминал не тянет
// (нет VT, перенаправленный вывод, низкое окно) — печатаем разовый баннер, как раньше.
StickyHeader? sticky = null;
app.Lifetime.ApplicationStarted.Register(() =>
{
    var hubOpts = app.Services.GetRequiredService<IOptions<HubOptions>>().Value;
    var registry = app.Services.GetRequiredService<SessionRegistry>();
    var listen = string.Join(", ", listenUrls);
    var lanIp = HubStatusLine.FindLanIp();
    var startedAt = DateTimeOffset.Now;

    var surface = new SystemTerminalSurface(Console.Out);
    sticky = StickyHeader.TryStart(
        width => HubStatusLine.Render(new HubStatusContext(
            registry.GetActive(), listen, lanIp, hubOpts.KnowledgeBaseRoot,
            startedAt, DateTimeOffset.Now), width),
        new StickyOptions(Lines: 2, ConfigEnabled: hubOpts.StickyHeader),
        surface,
        SystemTerminalSurface.TryEnableVirtualTerminal(),
        consoleGate);

    if (sticky is null)
    {
        var panel = new Panel(new Rows(
                new Markup($"[grey]слушает:[/] {Markup.Escape(listen)}"),
                new Markup($"[grey]kb:[/] {Markup.Escape(hubOpts.KnowledgeBaseRoot)}   [grey]db:[/] {Markup.Escape(hubOpts.SqliteConnectionString)}")))
            .Header("[bold]sz-diag hub[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
    }
});

// Сброс области прокрутки при остановке — иначе консоль останется усечённой.
app.Lifetime.ApplicationStopping.Register(() => sticky?.Dispose());
AppDomain.CurrentDomain.ProcessExit += (_, _) => sticky?.Dispose();
```

**Заменить** строку `app.Run();` (строка 89) на:

```csharp
try { app.Run(); }
finally { sticky?.Dispose(); }
```

- [ ] **Step 3: Собрать и прогнать все тесты**

Run: `dotnet build; dotnet test`
Expected: сборка без ошибок, все ~174+ тестов зелёные.

- [ ] **Step 4: Проверить руками**

Run: `dotnet run --project src/SzDiag.Hub` в Windows Terminal.
Expected: сверху две строки статуса + серая линия, они **не уезжают** при появлении логов; логи скроллятся под линией. `Ctrl+C` → консоль после выхода нормальная (следующая команда печатается со штатным скроллом).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hub
git commit -m "feat(hub): липкая панель статуса в консоли"
```

---

### Task 8: Строки статуса агента

**Files:**
- Create: `src/SzDiag.Agent/AgentStatusLine.cs`
- Create: `tests/SzDiag.Agent.Tests/AgentStatusLineTests.cs`
- Modify: `src/SzDiag.Agent/SzDiag.Agent.csproj`

- [ ] **Step 1: Подключить ConsoleUi к агенту**

В `src/SzDiag.Agent/SzDiag.Agent.csproj` добавить в `ItemGroup` с `ProjectReference`:

```xml
    <ProjectReference Include="..\SzDiag.ConsoleUi\SzDiag.ConsoleUi.csproj" />
```

- [ ] **Step 2: Написать падающий тест**

Создать `tests/SzDiag.Agent.Tests/AgentStatusLineTests.cs`:

```csharp
using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

public class AgentStatusLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AgentStatusContext Ctx(
        DateTimeOffset? lastHeartbeat = null,
        DateTimeOffset? watchdogAt = null,
        DateTimeOffset? bootTime = null,
        string mode = "") => new(
        Sz: "156864",
        HubUrl: "http://192.168.1.10:5099",
        SshPort: 2222,
        WatchdogAt: watchdogAt ?? Now + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(42),
        BootTime: bootTime ?? Now - TimeSpan.FromHours(5) - TimeSpan.FromMinutes(12),
        LastHeartbeatOk: lastHeartbeat ?? Now - TimeSpan.FromSeconds(5),
        HeartbeatTimeout: TimeSpan.FromSeconds(60),
        Mode: mode,
        Now: Now);

    private static string Plain(string markup) => SzDiag.ConsoleUi.MarkupText.Plain(markup);

    [Fact]
    public void Render_FirstLine_HasSzHubPortWatchdog()
    {
        var lines = AgentStatusLine.Render(Ctx(), width: 120);
        Assert.Equal(2, lines.Count);
        var first = Plain(lines[0]);
        Assert.Contains("СЗ 156864", first);
        Assert.Contains("192.168.1.10:5099", first);
        Assert.Contains("2222", first);
        Assert.Contains("3ч 42мин", first);
    }

    [Fact]
    public void Render_SecondLine_HasHotkeysAndUptime()
    {
        var second = Plain(AgentStatusLine.Render(Ctx(), width: 120)[1]);
        Assert.Contains("[C]", second);
        Assert.Contains("[Q]", second);
        Assert.Contains("5ч 12мин", second);
    }

    [Fact]
    public void Render_FreshHeartbeat_ShowsOnline() =>
        Assert.Contains("online", Plain(AgentStatusLine.Render(Ctx(), width: 120)[0]));

    [Fact]
    public void Render_StaleHeartbeat_ShowsReconnecting()
    {
        var lines = AgentStatusLine.Render(Ctx(lastHeartbeat: Now - TimeSpan.FromSeconds(90)), width: 120);
        var first = Plain(lines[0]);
        Assert.Contains("переподключение", first);
        Assert.DoesNotContain("online", first);
    }

    [Fact]
    public void Render_NoHeartbeatYet_ShowsConnecting()
    {
        var ctx = Ctx() with { LastHeartbeatOk = null };
        Assert.Contains("подключение", Plain(AgentStatusLine.Render(ctx, width: 120)[0]));
    }

    [Fact]
    public void Render_NoWatchdog_ShowsDash()
    {
        var ctx = Ctx() with { WatchdogAt = null };
        Assert.Contains("watchdog —", Plain(AgentStatusLine.Render(ctx, width: 120)[0]));
    }

    [Fact]
    public void Render_ExpiredWatchdog_ClampsToZero()
    {
        var ctx = Ctx(watchdogAt: Now - TimeSpan.FromMinutes(5));
        Assert.Contains("watchdog 0сек", Plain(AgentStatusLine.Render(ctx, width: 120)[0]));
    }

    [Fact]
    public void Render_Mode_IsShownWhenSet()
    {
        var first = Plain(AgentStatusLine.Render(Ctx(mode: "WinPE"), width: 120)[0]);
        Assert.Contains("WinPE", first);
    }

    [Fact]
    public void Render_NeverExceedsWidth()
    {
        foreach (var width in new[] { 40, 60, 80, 120, 200 })
        foreach (var line in AgentStatusLine.Render(Ctx(mode: "WinPE"), width))
            Assert.True(Plain(line).Length <= width,
                $"ширина {width}: строка длиной {Plain(line).Length}");
    }

    [Fact]
    public void Render_VeryNarrow_DoesNotThrow() =>
        Assert.Null(Record.Exception(() => AgentStatusLine.Render(Ctx(), width: 5)));
}
```

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AgentStatusLine`
Expected: FAIL — `AgentStatusLine`/`AgentStatusContext` не существуют.

- [ ] **Step 4: Реализовать**

Создать `src/SzDiag.Agent/AgentStatusLine.cs`:

```csharp
using Spectre.Console;
using SzDiag.ConsoleUi;

namespace SzDiag.Agent;

/// <summary>Состояние агента для панели. Пересобирается на каждом тике перерисовки.</summary>
/// <param name="WatchdogAt">Момент срабатывания watchdog. null — watchdog не ставился (WinPE).</param>
/// <param name="LastHeartbeatOk">Последний удавшийся heartbeat. null — ни одного ещё не было.</param>
/// <param name="Mode">Пометка режима («WinPE») или пусто для обычного.</param>
public sealed record AgentStatusContext(
    string Sz,
    string HubUrl,
    int SshPort,
    DateTimeOffset? WatchdogAt,
    DateTimeOffset? BootTime,
    DateTimeOffset? LastHeartbeatOk,
    TimeSpan HeartbeatTimeout,
    string Mode,
    DateTimeOffset Now);

/// <summary>Две строки статуса агента. Обязана уложиться в заданную ширину.</summary>
public static class AgentStatusLine
{
    public static IReadOnlyList<string> Render(AgentStatusContext ctx, int width)
    {
        var mode = string.IsNullOrEmpty(ctx.Mode) ? "" : $" [grey]({Markup.Escape(ctx.Mode)})[/]";
        var hub = StripScheme(ctx.HubUrl);

        var first = $"[bold]СЗ {Markup.Escape(ctx.Sz)}[/]{mode}  {Link(ctx)}" +
                    $"   [grey]hub[/] {Markup.Escape(hub)}" +
                    $"   [grey]sshd[/] :{ctx.SshPort}" +
                    $"   [grey]watchdog[/] {Watchdog(ctx)}";

        var uptime = ctx.BootTime is { } boot ? Elapsed.Format(ctx.Now - boot) : "—";
        var second = $"[green][[C]][/] закрыть СЗ   [grey][[Q]][/] выход" +
                     $"   [grey]uptime[/] {uptime}";

        return new[] { Fit(first, width), Fit(second, width) };
    }

    /// <summary>Статус связи с hub по свежести последнего heartbeat: свой признак, потому
    /// что SignalR молча переподключается и «живой объект» ничего не доказывает.</summary>
    private static string Link(AgentStatusContext ctx)
    {
        if (ctx.LastHeartbeatOk is not { } last) return "[yellow]● подключение…[/]";
        return ctx.Now - last <= ctx.HeartbeatTimeout
            ? "[green]● online[/]"
            : "[yellow]● переподключение[/]";
    }

    private static string Watchdog(AgentStatusContext ctx)
    {
        if (ctx.WatchdogAt is not { } at) return "—";
        return Elapsed.Format(at - ctx.Now);   // Elapsed сам зажимает отрицательное в 0
    }

    /// <summary>«http://192.168.1.10:5099» → «192.168.1.10:5099» — схема в панели только ест ширину.</summary>
    private static string StripScheme(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        var s = i >= 0 ? url[(i + 3)..] : url;
        return s.TrimEnd('/');
    }

    private static string Fit(string markup, int width) => MarkupText.Fit(markup, width);
}
```

**Про экранирование:** `[[C]]` в Spectre-разметке — это литеральные `[C]`. Считать длину и
резать такие строки умеет только `MarkupText` (Task 4); наивная регулярка здесь врёт.

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AgentStatusLine`
Expected: PASS, 10 тестов.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Agent/AgentStatusLine.cs src/SzDiag.Agent/SzDiag.Agent.csproj tests/SzDiag.Agent.Tests/AgentStatusLineTests.cs
git commit -m "feat(agent): строки статуса для липкой панели"
```

---

### Task 9: Включить панель в агенте

Панель нужна в двух интерактивных ветках: обычной и `--pe`. В `--revert` и `--resume` она не включается — там нет консоли (запуск из scheduled task), фоллбэк сработает сам по `OutputRedirected`.

**Files:**
- Modify: `src/SzDiag.Agent/AgentOptions.cs`
- Modify: `src/SzDiag.Agent/AgentCommandWiring.cs:90-99`
- Modify: `src/SzDiag.Agent/Program.cs:29-39`, `:186-213`, `:139-215` (ветка `--pe`)

- [ ] **Step 1: Добавить настройку в AgentOptions**

В `src/SzDiag.Agent/AgentOptions.cs` добавить в конец класса:

```csharp
    /// <summary>Липкая панель статуса в верхних строках консоли. false — обычный
    /// линейный вывод. Переопределяется через SZAGENT_StickyHeader.</summary>
    public bool StickyHeader { get; set; } = true;
```

- [ ] **Step 2: Написать падающий тест на признак живого heartbeat**

Панели нужен момент последнего удавшегося heartbeat, а цикл сейчас глотает исключения молча. Добавляем необязательный колбэк.

Создать `tests/SzDiag.Agent.Tests/HeartbeatLoopCallbackTests.cs`:

```csharp
using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

public class HeartbeatLoopCallbackTests
{
    /// <summary>Минимальный IHubLink: считает heartbeat'ы и по требованию падает.
    /// Сигнатуры — по src/SzDiag.Agent/IHubLink.cs (11 членов, все обязательны).</summary>
    private sealed class CountingLink : IHubLink
    {
        private readonly bool _throw;
        public int Calls;
        public CountingLink(bool shouldThrow) => _throw = shouldThrow;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (_throw) throw new InvalidOperationException("канал лёг");
            return Task.CompletedTask;
        }
        public void OnRevert(Func<string, Task> handler) { }
        public void OnRunTests(Func<string, string?, Task> handler) { }
        public void OnRunDiag(Func<string, string?, Task> handler) { }
        public void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler) { }
        public Task SendExecResultAsync(SzDiag.Contracts.ExecResult result,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since,
            CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopAccessManager : ISystemAccessManager
    {
        public RevertState Open(AccessSpec spec) => new() { Sz = spec.Sz };
        public void Revert(RevertState state) { }
        public void Resume(RevertState state, AccessSpec spec) { }
    }

    /// <summary>StartAsync не зовём: HeartbeatOnceAsync дёргает link напрямую.</summary>
    private static AgentSession MakeSession(IHubLink link) =>
        new(new NoopAccessManager(), link,
            new AccessSpec("156864", "svc-diag", "ssh-ed25519 AAAA...", 22, TimeSpan.FromHours(6)),
            "PC-1");

    [Fact]
    public async Task Callback_FiresTrue_OnSuccess()
    {
        var link = new CountingLink(shouldThrow: false);
        var ok = false;
        using var cts = new CancellationTokenSource();
        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token,
            success => { if (success) ok = true; });

        while (link.Calls == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.True(ok);
    }

    [Fact]
    public async Task Callback_FiresFalse_OnFailure()
    {
        var link = new CountingLink(shouldThrow: true);
        bool? seen = null;
        using var cts = new CancellationTokenSource();
        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token,
            success => seen ??= success);

        while (link.Calls == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.False(seen);
    }

    [Fact]
    public async Task NoCallback_StillWorks()
    {
        var link = new CountingLink(shouldThrow: false);
        using var cts = new CancellationTokenSource();
        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token);
        while (link.Calls == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
        Assert.True(link.Calls >= 1);
    }
}
```

Фейки написаны заново, а не переиспользованы из `AgentSessionTests` — тамошние `FakeHubLink`/`FakeManager` объявлены `private` внутри своего класса и наружу не видны. Здесь нужен именно падающий heartbeat, чего тот фейк не умеет.

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HeartbeatLoopCallback`
Expected: FAIL — у `StartHeartbeatLoop` нет четвёртого параметра.

- [ ] **Step 4: Добавить колбэк в heartbeat-цикл**

В `src/SzDiag.Agent/AgentCommandWiring.cs` **заменить** метод `StartHeartbeatLoop` (строки 90-99) на:

```csharp
    /// <param name="onResult">Необязательный колбэк с исходом каждой попытки — панель
    /// статуса по нему отличает живой канал от переподключения.</param>
    public static Task StartHeartbeatLoop(AgentSession session, int heartbeatSeconds,
        CancellationToken ct, Action<bool>? onResult = null) =>
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var ok = false;
                try { await session.HeartbeatOnceAsync(ct); ok = true; }
                catch { /* переподключение SignalR */ }
                try { onResult?.Invoke(ok); } catch { /* панель не должна ронять цикл */ }
                try { await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        });
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HeartbeatLoopCallback`
Expected: PASS, 3 теста.

- [ ] **Step 6: Завести общий лок вокруг консоли агента**

В `src/SzDiag.Agent/Program.cs` добавить к using'ам:

```csharp
using SzDiag.ConsoleUi;
```

**Заменить** строки 29-34 (от `var rawOut = Console.Out;` до создания `term`) на:

```csharp
// Единый лок на запись в консоль: панель перерисовывается из таймер-потока, логи и
// вывод Spectre — из своих. Оборачиваем именно rawOut, чтобы под локом оказались оба
// пути вывода (Tee для логов и Spectre для цветного).
var consoleGate = new object();
var rawOut = new SyncedConsoleWriter(Console.Out, consoleGate);
Console.SetOut(new TeeTextWriter(rawOut, logFile));

// Цветной вывод (Spectre.Console) идёт напрямую в реальную консоль, минуя Tee — иначе в
// лог-файл попадали бы сырые ANSI-коды. Announce() дублирует туда же чистый текст без разметки.
var term = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(rawOut) });
```

- [ ] **Step 7: Включить панель в основной ветке**

В `src/SzDiag.Agent/Program.cs` **после** блока `AgentCommandWiring.RegisterHandlers(...)` (строка ~199) и **до** `using var closeGuard = ...` вставить:

```csharp
// Липкая панель статуса. Момент открытия доступа — точка отсчёта watchdog.
var openedAt = DateTimeOffset.Now;
DateTimeOffset? lastHeartbeatOk = null;
var bootTime = BootTimeReader.Read(ps);
var sticky = StickyHeader.TryStart(
    width => AgentStatusLine.Render(new AgentStatusContext(
        Sz: sz,
        HubUrl: hubUrl,
        SshPort: opts.SshPort,
        WatchdogAt: openedAt + TimeSpan.FromHours(opts.WatchdogHours),
        BootTime: bootTime,
        LastHeartbeatOk: lastHeartbeatOk,
        HeartbeatTimeout: TimeSpan.FromSeconds(opts.HeartbeatSeconds * 3),
        Mode: "",
        Now: DateTimeOffset.Now), width),
    new StickyOptions(Lines: 2, ConfigEnabled: opts.StickyHeader),
    new SystemTerminalSurface(rawOut),
    SystemTerminalSurface.TryEnableVirtualTerminal(),
    consoleGate);
```

**Заменить** heartbeat-строку (было `var heartbeat = AgentCommandWiring.StartHeartbeatLoop(session, (int)opts.HeartbeatSeconds, cts.Token);`) на:

```csharp
var heartbeat = AgentCommandWiring.StartHeartbeatLoop(session, (int)opts.HeartbeatSeconds,
    cts.Token, ok => { if (ok) lastHeartbeatOk = DateTimeOffset.Now; });
```

**Заменить** две строки с подсказкой хоткеев (были `term.MarkupLine("\n[green][[C]][/] Закрыть СЗ и откатить ...")` и парная `logFile.WriteLine(...)`) на:

```csharp
// При липкой панели хоткеи живут в ней — в поток их не печатаем, чтобы не дублировать.
if (sticky is null)
    term.MarkupLine("\n[green][[C]][/] Закрыть СЗ и откатить    [grey][[Q]][/] Выход без отката (не рекомендуется)");
logFile.WriteLine("\n[C] Закрыть СЗ и откатить    [Q] Выход без отката (не рекомендуется)");
```

В блоке `finally` (строки ~243-246) добавить сброс панели **первым**, до `logFile.Flush()`:

```csharp
finally
{
    sticky?.Dispose();
    logFile.Flush();
}
```

**Внимание:** `sticky` объявлена внутри `try`, а `finally` её не видит. Объявить `StickyHeader? sticky = null;` **до** блока `try` (перед `term.Write(new Rule(...))` на строке ~218), а в теле присваивать без `var`.

Дополнительно — сброс при закрытии окна крестиком, в существующем `ConsoleCloseGuard`:

```csharp
using var closeGuard = new ConsoleCloseGuard(() =>
{
    sticky?.Dispose();
    session.RevertAsync().GetAwaiter().GetResult();
});
```

- [ ] **Step 8: Включить панель в ветке --pe**

В ветке `--pe` (`src/SzDiag.Agent/Program.cs:139-215`) **после** `AgentCommandWiring.RegisterHandlers(...)` вставить:

```csharp
// Панель в PE: watchdog не ставится (в PE нет Task Scheduler — см. WinPeAccessManager),
// поэтому WatchdogAt = null и в панели будет прочерк.
DateTimeOffset? peLastHeartbeatOk = null;
var peSticky = StickyHeader.TryStart(
    width => AgentStatusLine.Render(new AgentStatusContext(
        Sz: peSz,
        HubUrl: peHubUrl,
        SshPort: peOpts.SshPort,
        WatchdogAt: null,
        BootTime: BootTimeReader.Read(ps),
        LastHeartbeatOk: peLastHeartbeatOk,
        HeartbeatTimeout: TimeSpan.FromSeconds(peOpts.HeartbeatSeconds * 3),
        Mode: "WinPE",
        Now: DateTimeOffset.Now), width),
    new StickyOptions(Lines: 2, ConfigEnabled: peOpts.StickyHeader),
    new SystemTerminalSurface(rawOut),
    SystemTerminalSurface.TryEnableVirtualTerminal(),
    consoleGate);
```

**Заменить** heartbeat-строку PE-ветки на:

```csharp
var peHeartbeat = AgentCommandWiring.StartHeartbeatLoop(peSession, (int)peOpts.HeartbeatSeconds,
    peCts.Token, ok => { if (ok) peLastHeartbeatOk = DateTimeOffset.Now; });
```

**Заменить** строку подсказки хоткеев PE-ветки (`term.MarkupLine("\n[green][[C]][/] Закрыть СЗ    [grey][[Q]][/] Выход");`) на:

```csharp
if (peSticky is null) term.MarkupLine("\n[green][[C]][/] Закрыть СЗ    [grey][[Q]][/] Выход");
```

Перед `return 0;` этой ветки (после `logFile.Flush();`) добавить `peSticky?.Dispose();` — **до** `return`.

- [ ] **Step 9: Собрать и прогнать все тесты**

Run: `dotnet build; dotnet test`
Expected: сборка без ошибок, все тесты зелёные.

- [ ] **Step 10: Коммит**

```bash
git add src/SzDiag.Agent tests/SzDiag.Agent.Tests
git commit -m "feat(agent): липкая панель статуса в консоли"
```

---

### Task 10: Документация

**Files:**
- Modify: `docs/TESTING.md`
- Modify: `CLAUDE.md`
- Modify: `docs/dev-backlog.md`

- [ ] **Step 1: Добавить раздел ручной проверки в TESTING.md**

Дописать в `docs/TESTING.md` новый раздел (следовать структуре и стилю соседних разделов файла — сверить перед вставкой):

```markdown
## Липкая панель статуса (hub / агент)

Автотесты покрывают сборку строк и решение о фоллбэке; сами escape-последовательности
и ресайз проверяются только руками.

1. **Hub в Windows Terminal:** `dist\host\start-hub.cmd` — сверху две строки статуса и
   серая линия, под ней скроллятся логи. Панель не уезжает.
2. **Живой счётчик:** подключить агента — в панели появляется `онлайн 1: <СЗ>`; закрыть
   СЗ (`szcli close <СЗ>`) — счётчик уходит в «нет активных СЗ».
3. **Ресайз:** потянуть окно по ширине и высоте — панель перестраивается, логи
   продолжают скроллиться только под линией.
4. **Выход:** `Ctrl+C` — после завершения консоль нормальная (следующая команда
   печатается со штатным скроллом, а не в нижней трети окна).
5. **Агент под нагрузкой:** запустить OCCT через `szcli test run` — панель обновляется,
   watchdog тикает вниз, `[C]`/`[Q]` работают.
6. **Headless:** `agent.exe --resume <state.json>` из scheduled task — панели нет,
   `agent.log` пишется как раньше.
7. **Старый conhost:** запустить hub в `conhost.exe` (не Windows Terminal) — либо панель,
   либо чистый линейный вывод, но **не** мусор из escape-кодов.
8. **Рубильник:** `"StickyHeader": false` в секции `Hub` файла `appsettings.json` —
   возвращается прежний разовый баннер.
```

- [ ] **Step 2: Обновить CLAUDE.md**

В разделе «Архитектура» добавить `SzDiag.ConsoleUi` в перечень проектов (сейчас там «Семь проектов в `src/`» — станет восемь; поправить и число):

```markdown
- **SzDiag.ConsoleUi** — консольный UI, общий для hub и агента. `StickyHeader` — липкая
  панель статуса в верхних строках через ANSI scroll region (DECSTBM): логи скроллятся
  под ней обычным потоком, перехват логов не нужен. `SyncedConsoleWriter` — общий лок на
  запись в консоль (панель рисуется из таймер-потока). Фоллбэк в линейный вывод при
  перенаправленном выводе, отсутствии VT, окне ниже 10 строк или `StickyHeader: false`
  в конфиге. Строки статуса поставляют `HubStatusLine`/`AgentStatusLine` в своих проектах.
```

- [ ] **Step 3: Отметить в бэклоге**

Если в `docs/dev-backlog.md` есть пункт про статус в консоли — отметить сделанным со ссылкой на спеку. Если нет — ничего не добавлять.

- [ ] **Step 4: Финальная проверка**

Run: `dotnet build; dotnet test`
Expected: сборка без ошибок, все тесты зелёные.

- [ ] **Step 5: Коммит**

```bash
git add docs CLAUDE.md
git commit -m "docs: липкая панель статуса — ручная проверка и описание проекта"
```

---

## Проверка соответствия спеке

| Требование спеки | Задача |
|---|---|
| ANSI scroll region, резерв строк, save/restore курсора | 4, 5 |
| Сброс региона на выходе (`Dispose`, `ProcessExit`, `ConsoleCloseGuard`) | 5, 7, 9 |
| Общий лок на запись в консоль | 3, 7, 9 |
| Панель хаба: адрес, LAN-IP, аптайм, онлайн-СЗ с активностью, kb-хвост | 6 |
| Панель агента: СЗ, статус коннекта, hub, sshd, watchdog, uptime, хоткеи | 8, 9 |
| Обрезка списка СЗ с `+N`, отбрасывание kb при нехватке ширины | 6 |
| Фоллбэк: перенаправление, нет VT, высота < 10, конфиг | 1, 5, 7, 9 |
| Ресайз: сверка размеров на тике, выключение при схлопывании окна | 5 |
| `FormatElapsed` переезжает в ConsoleUi, CLI ссылается | 2 |
| Панель в `--pe`; в `--resume` не включается (фоллбэк по redirected) | 9 |
| Ручная проверка в `docs/TESTING.md` | 10 |
