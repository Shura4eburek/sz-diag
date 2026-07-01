# sz-diag Фаза 1 — План 1: серверный фундамент (Contracts + Hub)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Рабочий центральный hub, к которому агенты подключаются по SignalR, регистрируют СЗ, шлют heartbeat; hub ведёт реестр, персистит сессии в SQLite, создаёт каркас базы знаний и помечает пропавшие сессии офлайн.

**Architecture:** ASP.NET Core приложение с SignalR-хабом `AgentHub`. Вся доменная логика вынесена в тестируемые сервисы: `SessionRegistry` (реестр в памяти), `ISessionStore`/`SqliteSessionStore` (персистенс), `IKnowledgeBaseScaffolder` (каркас kb). SignalR-хаб — тонкий слой, делегирующий сервисам. Фоновый `OfflineSweeper` метит сессии офлайн по таймауту heartbeat.

**Tech Stack:** .NET 8, C#, ASP.NET Core SignalR, Microsoft.Data.Sqlite, xUnit, Microsoft.AspNetCore.Mvc.Testing.

Спеки: [../specs/2026-07-01-sz-diag-phase1-design.md](../specs/2026-07-01-sz-diag-phase1-design.md), [../specs/2026-07-01-kb-obsidian-design.md](../specs/2026-07-01-kb-obsidian-design.md)

---

## File Structure

```
SzDiag.sln
src/
  SzDiag.Contracts/
    SzDiag.Contracts.csproj
    SessionStatus.cs        — enum Online/Offline
    SessionInfo.cs          — снимок активной сессии (для реестра/CLI)
    SessionRecord.cs        — запись для персистенса/истории
    RegisterRequest.cs      — payload регистрации агента
    HubRoutes.cs            — константы имён методов/маршрута/заголовка токена
  SzDiag.Hub/
    SzDiag.Hub.csproj
    Program.cs              — DI, конфиг, эндпоинт SignalR, запуск
    AgentHub.cs             — SignalR-хаб (тонкий)
    SessionRegistry.cs      — потокобезопасный реестр активных сессий
    ISessionStore.cs        — контракт персистенса
    SqliteSessionStore.cs   — SQLite-реализация
    IKnowledgeBaseScaffolder.cs
    KnowledgeBaseScaffolder.cs — создание каркаса kb/СЗ/<sz>/
    OfflineSweeper.cs       — BackgroundService: офлайн по таймауту
    HubOptions.cs           — опции (токен, пути, таймауты)
tests/
  SzDiag.Hub.Tests/
    SzDiag.Hub.Tests.csproj
    SessionRegistryTests.cs
    SqliteSessionStoreTests.cs
    KnowledgeBaseScaffolderTests.cs
    OfflineSweeperTests.cs
    AgentHubIntegrationTests.cs
```

Каждый файл — одна ответственность. Доменные сервисы не знают про SignalR, поэтому тестируются напрямую; SignalR покрыт одним интеграционным тестом.

---

### Task 0: Скелет решения и проектов

**Files:**
- Create: `SzDiag.sln`, `src/SzDiag.Contracts/SzDiag.Contracts.csproj`, `src/SzDiag.Hub/SzDiag.Hub.csproj`, `tests/SzDiag.Hub.Tests/SzDiag.Hub.Tests.csproj`

- [ ] **Step 1: Создать решение и проекты**

Run:
```bash
dotnet new sln -n SzDiag
dotnet new classlib -n SzDiag.Contracts -o src/SzDiag.Contracts -f net8.0
dotnet new web -n SzDiag.Hub -o src/SzDiag.Hub -f net8.0
dotnet new xunit -n SzDiag.Hub.Tests -o tests/SzDiag.Hub.Tests -f net8.0
dotnet sln add src/SzDiag.Contracts src/SzDiag.Hub tests/SzDiag.Hub.Tests
dotnet add src/SzDiag.Hub reference src/SzDiag.Contracts
dotnet add tests/SzDiag.Hub.Tests reference src/SzDiag.Hub src/SzDiag.Contracts
dotnet add src/SzDiag.Hub package Microsoft.Data.Sqlite
dotnet add tests/SzDiag.Hub.Tests package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: Удалить шаблонные файлы**

Удалить `src/SzDiag.Contracts/Class1.cs` и `tests/SzDiag.Hub.Tests/UnitTest1.cs`.

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: скелет решения SzDiag (Contracts, Hub, Hub.Tests)"
```

---

### Task 1: Контракты (DTO и константы)

**Files:**
- Create: `src/SzDiag.Contracts/SessionStatus.cs`, `SessionInfo.cs`, `SessionRecord.cs`, `RegisterRequest.cs`, `HubRoutes.cs`

DTO без поведения — тесты не нужны; они проверяются через сервисы в следующих задачах.

- [ ] **Step 1: Написать типы**

`src/SzDiag.Contracts/SessionStatus.cs`:
```csharp
namespace SzDiag.Contracts;

public enum SessionStatus
{
    Online,
    Offline
}
```

`src/SzDiag.Contracts/SessionInfo.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>Снимок активной сессии СЗ для реестра и CLI.</summary>
public sealed record SessionInfo(
    string Sz,
    string Ip,
    string Hostname,
    SessionStatus Status,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastHeartbeat);
```

`src/SzDiag.Contracts/SessionRecord.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>Запись для персистенса и истории открытий/закрытий.</summary>
public sealed record SessionRecord(
    string Sz,
    string Ip,
    string Hostname,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt);
```

`src/SzDiag.Contracts/RegisterRequest.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>Payload регистрации агента. IP берётся из соединения, не из payload.</summary>
public sealed record RegisterRequest(string Sz, string Hostname);
```

`src/SzDiag.Contracts/HubRoutes.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>Имена, общие для агента и hub, чтобы не расходились строки.</summary>
public static class HubRoutes
{
    public const string Path = "/agents";

    // Заголовок с pre-shared токеном при коннекте.
    public const string TokenHeader = "X-SzDiag-Token";

    // Методы, которые агент вызывает на hub.
    public const string Register = nameof(Register);
    public const string Heartbeat = nameof(Heartbeat);

    // Метод, который hub вызывает на агенте (client method).
    public const string Revert = nameof(Revert);
}
```

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build src/SzDiag.Contracts`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Contracts
git commit -m "feat(contracts): DTO сессий и константы протокола hub"
```

---

### Task 2: SessionRegistry — реестр активных сессий

**Files:**
- Create: `src/SzDiag.Hub/SessionRegistry.cs`
- Test: `tests/SzDiag.Hub.Tests/SessionRegistryTests.cs`

Реестр связывает СЗ ↔ connectionId, хранит снимок сессии, потокобезопасен.

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Hub.Tests/SessionRegistryTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionRegistryTests
{
    private static SessionRegistry NewRegistry() => new();

    [Fact]
    public void Register_AddsOnlineSession()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var active = reg.GetActive();
        var s = Assert.Single(active);
        Assert.Equal("156864", s.Sz);
        Assert.Equal("10.0.0.42", s.Ip);
        Assert.Equal("PC-1", s.Hostname);
        Assert.Equal(SessionStatus.Online, s.Status);
    }

    [Fact]
    public void Register_SameSzTwice_ReplacesConnection()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.Register("156864", "10.0.0.43", "PC-1", "conn-2");

        Assert.Single(reg.GetActive());
        Assert.Equal("conn-2", reg.TryGetConnectionId("156864"));
    }

    [Fact]
    public void Heartbeat_UpdatesLastHeartbeatAndSetsOnline()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkOfflineByConnection("conn-1");

        var updated = reg.Heartbeat("156864");

        Assert.True(updated);
        Assert.Equal(SessionStatus.Online, reg.GetActive().Single().Status);
    }

    [Fact]
    public void Heartbeat_UnknownSz_ReturnsFalse()
    {
        var reg = NewRegistry();
        Assert.False(reg.Heartbeat("000000"));
    }

    [Fact]
    public void MarkOfflineByConnection_SetsStatusOffline()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var sz = reg.MarkOfflineByConnection("conn-1");

        Assert.Equal("156864", sz);
        Assert.Equal(SessionStatus.Offline, reg.GetActive().Single().Status);
    }

    [Fact]
    public void Remove_DeletesSession()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        reg.Remove("156864");

        Assert.Empty(reg.GetActive());
        Assert.Null(reg.TryGetConnectionId("156864"));
    }

    [Fact]
    public void TryGetConnectionId_UnknownSz_ReturnsNull()
    {
        var reg = NewRegistry();
        Assert.Null(reg.TryGetConnectionId("000000"));
    }
}
```

- [ ] **Step 2: Запустить тесты — убедиться, что не компилируются/падают**

Run: `dotnet test tests/SzDiag.Hub.Tests`
Expected: FAIL — тип `SessionRegistry` не существует.

- [ ] **Step 3: Реализовать SessionRegistry**

`src/SzDiag.Hub/SessionRegistry.cs`:
```csharp
using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Потокобезопасный реестр активных сессий СЗ. Один экземпляр (singleton).</summary>
public sealed class SessionRegistry
{
    private sealed record Entry(SessionInfo Info, string ConnectionId);

    private readonly ConcurrentDictionary<string, Entry> _bySz = new();
    private readonly TimeProvider _time;

    public SessionRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Register(string sz, string ip, string hostname, string connectionId)
    {
        var now = _time.GetUtcNow();
        var info = new SessionInfo(sz, ip, hostname, SessionStatus.Online, now, now);
        _bySz[sz] = new Entry(info, connectionId);
    }

    public bool Heartbeat(string sz)
    {
        if (!_bySz.TryGetValue(sz, out var e)) return false;
        var now = _time.GetUtcNow();
        _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Online, LastHeartbeat = now } };
        return true;
    }

    public string? MarkOfflineByConnection(string connectionId)
    {
        foreach (var (sz, e) in _bySz)
        {
            if (e.ConnectionId != connectionId) continue;
            _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Offline } };
            return sz;
        }
        return null;
    }

    /// <summary>Пометить офлайн сессии, чей heartbeat старше порога. Возвращает затронутые СЗ.</summary>
    public IReadOnlyList<string> MarkStaleOffline(TimeSpan maxAge)
    {
        var cutoff = _time.GetUtcNow() - maxAge;
        var affected = new List<string>();
        foreach (var (sz, e) in _bySz)
        {
            if (e.Info.Status == SessionStatus.Online && e.Info.LastHeartbeat < cutoff)
            {
                _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Offline } };
                affected.Add(sz);
            }
        }
        return affected;
    }

    public void Remove(string sz) => _bySz.TryRemove(sz, out _);

    public string? TryGetConnectionId(string sz)
        => _bySz.TryGetValue(sz, out var e) ? e.ConnectionId : null;

    public IReadOnlyList<SessionInfo> GetActive()
        => _bySz.Values.Select(e => e.Info).ToList();
}
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests`
Expected: PASS (7 тестов).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Hub/SessionRegistry.cs tests/SzDiag.Hub.Tests/SessionRegistryTests.cs
git commit -m "feat(hub): SessionRegistry — потокобезопасный реестр сессий"
```

---

### Task 3: SQLite-персистенс сессий

**Files:**
- Create: `src/SzDiag.Hub/ISessionStore.cs`, `src/SzDiag.Hub/SqliteSessionStore.cs`
- Test: `tests/SzDiag.Hub.Tests/SqliteSessionStoreTests.cs`

- [ ] **Step 1: Написать контракт**

`src/SzDiag.Hub/ISessionStore.cs`:
```csharp
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Персистенс сессий: активные + история открытий/закрытий.</summary>
public interface ISessionStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task RecordOpenAsync(SessionRecord record, CancellationToken ct = default);
    Task RecordCloseAsync(string sz, DateTimeOffset closedAt, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Hub.Tests/SqliteSessionStoreTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-{Guid.NewGuid():N}.db");

    private async Task<SqliteSessionStore> NewStoreAsync()
    {
        var store = new SqliteSessionStore($"Data Source={_dbPath}");
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public async Task RecordOpen_ThenHistory_ReturnsOpenRecord()
    {
        var store = await NewStoreAsync();
        var opened = DateTimeOffset.UtcNow;
        await store.RecordOpenAsync(new SessionRecord("156864", "10.0.0.42", "PC-1", opened, null));

        var history = await store.GetHistoryAsync();
        var r = Assert.Single(history);
        Assert.Equal("156864", r.Sz);
        Assert.Null(r.ClosedAt);
    }

    [Fact]
    public async Task RecordClose_SetsClosedAt()
    {
        var store = await NewStoreAsync();
        var opened = DateTimeOffset.UtcNow;
        await store.RecordOpenAsync(new SessionRecord("156864", "10.0.0.42", "PC-1", opened, null));

        var closed = opened.AddMinutes(30);
        await store.RecordCloseAsync("156864", closed);

        var r = Assert.Single(await store.GetHistoryAsync());
        Assert.NotNull(r.ClosedAt);
        Assert.Equal(closed.ToUnixTimeSeconds(), r.ClosedAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task RecordOpen_SameSzAgain_AddsSecondHistoryRow()
    {
        var store = await NewStoreAsync();
        await store.RecordOpenAsync(new SessionRecord("156864", "10.0.0.42", "PC-1", DateTimeOffset.UtcNow, null));
        await store.RecordCloseAsync("156864", DateTimeOffset.UtcNow);
        await store.RecordOpenAsync(new SessionRecord("156864", "10.0.0.99", "PC-1", DateTimeOffset.UtcNow, null));

        Assert.Equal(2, (await store.GetHistoryAsync()).Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter SqliteSessionStoreTests`
Expected: FAIL — `SqliteSessionStore` не существует.

- [ ] **Step 4: Реализовать SqliteSessionStore**

`src/SzDiag.Hub/SqliteSessionStore.cs`:
```csharp
using Microsoft.Data.Sqlite;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>
/// SQLite-персистенс. Каждое открытие СЗ — отдельная строка истории; закрытие
/// проставляет closed_at последней незакрытой строке этой СЗ.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;

    public SqliteSessionStore(string connectionString) => _connectionString = connectionString;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                sz        TEXT    NOT NULL,
                ip        TEXT    NOT NULL,
                hostname  TEXT    NOT NULL,
                opened_at INTEGER NOT NULL,
                closed_at INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_sz ON sessions(sz);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordOpenAsync(SessionRecord record, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (sz, ip, hostname, opened_at, closed_at)
            VALUES ($sz, $ip, $host, $opened, NULL);
            """;
        cmd.Parameters.AddWithValue("$sz", record.Sz);
        cmd.Parameters.AddWithValue("$ip", record.Ip);
        cmd.Parameters.AddWithValue("$host", record.Hostname);
        cmd.Parameters.AddWithValue("$opened", record.OpenedAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordCloseAsync(string sz, DateTimeOffset closedAt, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET closed_at = $closed
            WHERE id = (
                SELECT id FROM sessions
                WHERE sz = $sz AND closed_at IS NULL
                ORDER BY id DESC LIMIT 1
            );
            """;
        cmd.Parameters.AddWithValue("$closed", closedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$sz", sz);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sz, ip, hostname, opened_at, closed_at FROM sessions ORDER BY id;";
        var result = new List<SessionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var closed = reader.IsDBNull(4)
                ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4));
            result.Add(new SessionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)), closed));
        }
        return result;
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter SqliteSessionStoreTests`
Expected: PASS (3 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hub/ISessionStore.cs src/SzDiag.Hub/SqliteSessionStore.cs tests/SzDiag.Hub.Tests/SqliteSessionStoreTests.cs
git commit -m "feat(hub): SQLite-персистенс сессий и истории"
```

---

### Task 4: Каркас базы знаний (Obsidian-форма)

**Files:**
- Create: `src/SzDiag.Hub/IKnowledgeBaseScaffolder.cs`, `src/SzDiag.Hub/KnowledgeBaseScaffolder.cs`
- Test: `tests/SzDiag.Hub.Tests/KnowledgeBaseScaffolderTests.cs`

Создаёт `kb/СЗ/<sz>/` с home-заметкой (frontmatter с автодатой), request/findings/actions и logs/. Идемпотентно: существующую папку не перетирает.

- [ ] **Step 1: Написать контракт**

`src/SzDiag.Hub/IKnowledgeBaseScaffolder.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>Создаёт каркас папки базы знаний для СЗ в Obsidian-форме.</summary>
public interface IKnowledgeBaseScaffolder
{
    /// <summary>Создаёт kb/СЗ/&lt;sz&gt;/ если её ещё нет. Возвращает путь к папке СЗ.</summary>
    string EnsureSkeleton(string sz);
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Hub.Tests/KnowledgeBaseScaffolderTests.cs`:
```csharp
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

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

        s.EnsureSkeleton("156864"); // повторно

        Assert.Equal("РУЧНОЙ ТЕКСТ", File.ReadAllText(reqPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter KnowledgeBaseScaffolderTests`
Expected: FAIL — `KnowledgeBaseScaffolder` не существует.

- [ ] **Step 4: Реализовать KnowledgeBaseScaffolder**

`src/SzDiag.Hub/KnowledgeBaseScaffolder.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>
/// Создаёт каркас kb/СЗ/&lt;sz&gt;/ в Obsidian-форме. Идемпотентно: если папка СЗ
/// уже есть — ничего не трогает (данные диагностики не перетираются).
/// </summary>
public sealed class KnowledgeBaseScaffolder : IKnowledgeBaseScaffolder
{
    private readonly string _kbRoot;
    private readonly Func<DateTimeOffset> _now;

    public KnowledgeBaseScaffolder(string kbRoot, Func<DateTimeOffset>? now = null)
    {
        _kbRoot = kbRoot;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string EnsureSkeleton(string sz)
    {
        var dir = Path.Combine(_kbRoot, "СЗ", sz);
        if (Directory.Exists(dir)) return dir;

        Directory.CreateDirectory(Path.Combine(dir, "logs"));

        var date = _now().ToString("yyyy-MM-dd");
        WriteIfMissing(Path.Combine(dir, $"{sz}.md"), HomeNote(sz, date));
        WriteIfMissing(Path.Combine(dir, "request.md"), $"# Дефект (со слов клиента) — СЗ {sz}\n\n");
        WriteIfMissing(Path.Combine(dir, "findings.md"), $"# Диагностика — СЗ {sz}\n\n");
        WriteIfMissing(Path.Combine(dir, "actions.md"), $"# Что заменили / сделали — СЗ {sz}\n\n");
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

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter KnowledgeBaseScaffolderTests`
Expected: PASS (3 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hub/IKnowledgeBaseScaffolder.cs src/SzDiag.Hub/KnowledgeBaseScaffolder.cs tests/SzDiag.Hub.Tests/KnowledgeBaseScaffolderTests.cs
git commit -m "feat(hub): каркас базы знаний в Obsidian-форме"
```

---

### Task 5: OfflineSweeper — офлайн по таймауту heartbeat

**Files:**
- Create: `src/SzDiag.Hub/HubOptions.cs`, `src/SzDiag.Hub/OfflineSweeper.cs`
- Test: `tests/SzDiag.Hub.Tests/OfflineSweeperTests.cs`

- [ ] **Step 1: Написать опции**

`src/SzDiag.Hub/HubOptions.cs`:
```csharp
namespace SzDiag.Hub;

public sealed class HubOptions
{
    /// <summary>Pre-shared токен, который агент шлёт в заголовке при коннекте.</summary>
    public string AgentToken { get; set; } = "";

    /// <summary>Строка подключения SQLite.</summary>
    public string SqliteConnectionString { get; set; } = "Data Source=szdiag.db";

    /// <summary>Корень базы знаний (Obsidian-vault).</summary>
    public string KnowledgeBaseRoot { get; set; } = "kb";

    /// <summary>Сессия помечается офлайн, если heartbeat старше этого порога.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Период проверки sweeper'ом.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);
}
```

- [ ] **Step 2: Написать падающий тест**

`tests/SzDiag.Hub.Tests/OfflineSweeperTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class OfflineSweeperTests
{
    [Fact]
    public void MarkStaleOffline_MarksSessionsWithOldHeartbeat()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        time.Advance(TimeSpan.FromSeconds(120));
        var affected = reg.MarkStaleOffline(TimeSpan.FromSeconds(60));

        Assert.Equal(new[] { "156864" }, affected);
        Assert.Equal(SessionStatus.Offline, reg.GetActive().Single().Status);
    }

    [Fact]
    public void MarkStaleOffline_FreshHeartbeat_NotMarked()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        time.Advance(TimeSpan.FromSeconds(30));
        var affected = reg.MarkStaleOffline(TimeSpan.FromSeconds(60));

        Assert.Empty(affected);
        Assert.Equal(SessionStatus.Online, reg.GetActive().Single().Status);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter OfflineSweeperTests`
Expected: FAIL — `MarkStaleOffline` уже реализован в Task 2, но `FakeTimeProvider` + опции компилируются; тест должен пройти сразу, если Task 2 сделан. Если падает — проверить, что `SessionRegistry` принимает `TimeProvider`.

- [ ] **Step 4: Реализовать OfflineSweeper (BackgroundService)**

`src/SzDiag.Hub/OfflineSweeper.cs`:
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Фоново метит офлайн сессии с протухшим heartbeat.</summary>
public sealed class OfflineSweeper : BackgroundService
{
    private readonly SessionRegistry _registry;
    private readonly HubOptions _options;

    public OfflineSweeper(SessionRegistry registry, IOptions<HubOptions> options)
    {
        _registry = registry;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _registry.MarkStaleOffline(_options.HeartbeatTimeout);
        }
    }
}
```

- [ ] **Step 5: Запустить тесты и сборку**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter OfflineSweeperTests`
Expected: PASS (2 теста).
Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hub/HubOptions.cs src/SzDiag.Hub/OfflineSweeper.cs tests/SzDiag.Hub.Tests/OfflineSweeperTests.cs
git commit -m "feat(hub): опции и фоновый sweeper офлайн-сессий"
```

---

### Task 6: AgentHub (SignalR) + сборка приложения

**Files:**
- Create: `src/SzDiag.Hub/AgentHub.cs`
- Modify: `src/SzDiag.Hub/Program.cs` (заменить шаблон целиком)
- Test: `tests/SzDiag.Hub.Tests/AgentHubIntegrationTests.cs`

- [ ] **Step 1: Реализовать AgentHub**

`src/SzDiag.Hub/AgentHub.cs`:
```csharp
using Microsoft.AspNetCore.SignalR;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>SignalR-хаб для агентов. Тонкий слой над сервисами.</summary>
public sealed class AgentHub : Hub
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IKnowledgeBaseScaffolder _kb;

    public AgentHub(SessionRegistry registry, ISessionStore store, IKnowledgeBaseScaffolder kb)
    {
        _registry = registry;
        _store = store;
        _kb = kb;
    }

    public async Task Register(RegisterRequest request)
    {
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _registry.Register(request.Sz, ip, request.Hostname, Context.ConnectionId);
        _kb.EnsureSkeleton(request.Sz);
        await _store.RecordOpenAsync(
            new SessionRecord(request.Sz, ip, request.Hostname, DateTimeOffset.UtcNow, null));
    }

    public Task Heartbeat(string sz)
    {
        _registry.Heartbeat(sz);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.MarkOfflineByConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
```

- [ ] **Step 2: Заменить Program.cs**

`src/SzDiag.Hub/Program.cs`:
```csharp
using Microsoft.AspNetCore.Http.Connections;
using SzDiag.Contracts;
using SzDiag.Hub;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HubOptions>(builder.Configuration.GetSection("Hub"));
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ISessionStore>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value;
    return new SqliteSessionStore(opts.SqliteConnectionString);
});
builder.Services.AddSingleton<IKnowledgeBaseScaffolder>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value;
    return new KnowledgeBaseScaffolder(opts.KnowledgeBaseRoot);
});
builder.Services.AddHostedService<OfflineSweeper>();
builder.Services.AddSignalR();

var app = builder.Build();

// Инициализация БД при старте.
await app.Services.GetRequiredService<ISessionStore>().InitializeAsync();

// Проверка pre-shared токена на коннекте к хабу.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments(HubRoutes.Path))
    {
        var expected = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value.AgentToken;
        var provided = ctx.Request.Headers[HubRoutes.TokenHeader].ToString();
        if (string.IsNullOrEmpty(expected) || provided != expected)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});

app.MapHub<AgentHub>(HubRoutes.Path, o => o.Transports = HttpTransportType.WebSockets);

app.Run();

// Для WebApplicationFactory в тестах.
public partial class Program { }
```

- [ ] **Step 3: Написать интеграционный тест**

`tests/SzDiag.Hub.Tests/AgentHubIntegrationTests.cs`:
```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AgentHubIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentHubIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", "Data Source=:memory:")
             .UseSetting("Hub:KnowledgeBaseRoot",
                 Path.Combine(Path.GetTempPath(), $"szkb-it-{Guid.NewGuid():N}")));
    }

    private HubConnection BuildConnection(string token)
    {
        var handler = _factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "agents", o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = token;
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .Build();
    }

    [Fact]
    public async Task Register_MakesSessionVisibleInRegistry()
    {
        var conn = BuildConnection("test-token");
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("156864", "PC-1"));

        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        Assert.Contains(registry.GetActive(), s => s.Sz == "156864" && s.Status == SessionStatus.Online);

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Hub_CanPushRevertToAgent()
    {
        var conn = BuildConnection("test-token");
        var reverted = new TaskCompletionSource<string>();
        conn.On<string>(HubRoutes.Revert, sz => reverted.TrySetResult(sz));

        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("156864", "PC-1"));

        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        var connId = registry.TryGetConnectionId("156864");
        Assert.NotNull(connId);

        var hub = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<AgentHub>>();
        await hub.Clients.Client(connId!).SendAsync(HubRoutes.Revert, "156864");

        var completed = await Task.WhenAny(reverted.Task, Task.Delay(5000));
        Assert.Same(reverted.Task, completed);
        Assert.Equal("156864", await reverted.Task);

        await conn.DisposeAsync();
    }
}
```

- [ ] **Step 4: Запустить весь набор тестов**

Run: `dotnet test`
Expected: PASS — все тесты (registry 7 + sqlite 3 + kb 3 + sweeper 2 + integration 2).

- [ ] **Step 5: Прогнать hub вручную (smoke)**

Создать `src/SzDiag.Hub/appsettings.Development.json`:
```json
{
  "Hub": {
    "AgentToken": "dev-token",
    "SqliteConnectionString": "Data Source=szdiag.db",
    "KnowledgeBaseRoot": "kb",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15"
  }
}
```

Run: `dotnet run --project src/SzDiag.Hub`
Expected: `Now listening on: http://localhost:5xxx`, приложение стартует без ошибок, создаётся `szdiag.db`.
Остановить: Ctrl+C.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(hub): SignalR AgentHub, DI, проверка токена, интеграционные тесты"
```

---

## Self-Review (выполнено при написании плана)

**Покрытие спеки:**
- Реестр СЗ→{IP,host,статус} → Task 2 (`SessionRegistry`). ✓
- Персистенс сессий/истории в SQLite → Task 3. ✓
- Каркас kb в Obsidian-форме (home-заметка + автодата) → Task 4. ✓
- Офлайн по пропаже heartbeat → Task 2/5 (`MarkStaleOffline` + `OfflineSweeper`). ✓
- Постоянное соединение + server-push `revert` → Task 6 (SignalR, тест push). ✓
- Pre-shared токен агент↔hub → Task 6 (middleware). ✓
- Создание kb при регистрации СЗ → Task 6 (`AgentHub.Register`). ✓

**Вне этого плана (следующие планы):** сам агент (План 3), CLI (`close`/`target`/список, План 2). Метод отдачи команды `revert` из CLI будет добавлен в План 2 через `IHubContext<AgentHub>` (уже проверено рабочим в интеграционном тесте Task 6).

**Плейсхолдеры:** отсутствуют — весь код приведён.
**Согласованность типов:** `HubRoutes.*`, `RegisterRequest(Sz,Hostname)`, `SessionInfo`, `SessionRecord` используются одинаково во всех задачах.
