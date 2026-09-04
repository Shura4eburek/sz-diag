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

            -- Вырубоны: заводятся по смене boot-time. Живут отдельно от sessions, потому что
            -- переживают и переподключение агента, и рестарт hub (in-memory реестр — нет).
            CREATE TABLE IF NOT EXISTS reboots (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                sz            TEXT    NOT NULL,
                at            INTEGER NOT NULL,
                prev_boot     INTEGER NULL,
                new_boot      INTEGER NULL,
                uptime_before INTEGER NULL,
                activity      TEXT    NULL,
                kind          TEXT    NULL,
                source        TEXT    NULL,
                bugcheck      INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reboots_sz ON reboots(sz);

            -- Окна ручных работ: вечернее выключение стенда, рубильник, перетык кабелей.
            -- Событие питания внутри окна — не дефект (бэклог п.100).
            CREATE TABLE IF NOT EXISTS maintenance (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                sz        TEXT    NOT NULL,
                from_at   INTEGER NOT NULL,
                until_at  INTEGER NOT NULL,
                reason    TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_maintenance_sz ON maintenance(sz);

            -- Метка конфигурации последнего прогона: «EXPO 6000, штатный БП» против «сток».
            -- Без неё повторный прогон нечитаем — не с чем сравнивать (СЗ 160697).
            CREATE TABLE IF NOT EXISTS test_config (
                sz     TEXT PRIMARY KEY,
                config TEXT NOT NULL,
                set_at INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Миграция для баз, заведённых до появления классификации (бэклог п.93): у старых
        // записей kind останется NULL и будет читаться как «неизвестно», а не как «кнопка».
        // duration_seconds — длительность сна для kind=sleep (бэклог п.140/222).
        foreach (var column in new[]
                 { "kind TEXT NULL", "source TEXT NULL", "bugcheck INTEGER NULL", "duration_seconds INTEGER NULL" })
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE reboots ADD COLUMN {column};";
            try { await alter.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* колонка уже есть — штатный случай */ }
        }
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

    public async Task RecordRebootAsync(RebootEvent evt, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reboots (sz, at, prev_boot, new_boot, uptime_before, activity, kind, source, bugcheck, duration_seconds)
            VALUES ($sz, $at, $prev, $new, $uptime, $activity, $kind, $source, $bugcheck, $duration);
            """;
        cmd.Parameters.AddWithValue("$kind", (object?)evt.Kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bugcheck", (object?)evt.Bugcheck ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", evt.Source);
        cmd.Parameters.AddWithValue("$sz", evt.Sz);
        cmd.Parameters.AddWithValue("$at", evt.At.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$prev", (object?)evt.PreviousBootTime?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$new", (object?)evt.NewBootTime?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uptime", (object?)evt.UptimeBeforeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity", (object?)evt.ActivityBefore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", (object?)evt.DurationSeconds ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RebootTimeline> GetRebootsAsync(string sz, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT at, prev_boot, new_boot, uptime_before, activity, kind, source, bugcheck, duration_seconds
            FROM reboots WHERE sz = $sz ORDER BY at, id;
            """;
        cmd.Parameters.AddWithValue("$sz", sz);

        var events = new List<RebootEvent>();
        long? maxUptime = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var uptime = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            if (uptime is { } u && (maxUptime is null || u > maxUptime)) maxUptime = u;
            events.Add(new RebootEvent(
                sz,
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                uptime,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? RebootSource.Heartbeat : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }
        await reader.CloseAsync();

        // С какого момента СЗ под наблюдением: без этой даты «вырубонов не зафиксировано»
        // читается как «их не было», хотя может значить «мы не смотрели» (бэклог п.97).
        await using var since = conn.CreateCommand();
        since.CommandText = "SELECT MIN(opened_at) FROM sessions WHERE sz = $sz;";
        since.Parameters.AddWithValue("$sz", sz);
        var raw = await since.ExecuteScalarAsync(ct);
        DateTimeOffset? watchingSince = raw is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(raw));

        // Окна ручных работ применяем на чтении, а не на записи: метку часто ставят задним
        // числом («вчера вечером гасили стенд»), и таймлайн должен переосмыслиться сразу.
        var windows = await ReadMaintenanceAsync(conn, sz, ct);
        if (windows.Count > 0)
        {
            for (var i = 0; i < events.Count; i++)
            {
                var w = windows.FirstOrDefault(x => x.Covers(events[i].At));
                if (w is null) continue;
                events[i] = events[i] with
                {
                    Kind = ShutdownKind.Maintenance,
                    ActivityBefore = string.IsNullOrWhiteSpace(events[i].ActivityBefore)
                        ? w.Reason
                        : $"{events[i].ActivityBefore} · {w.Reason}",
                };
            }
        }

        return new RebootTimeline(sz, events, maxUptime, watchingSince);
    }

    public async Task SetLastTestConfigAsync(string sz, string config, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO test_config (sz, config, set_at) VALUES ($sz, $config, $at)
            ON CONFLICT(sz) DO UPDATE SET config = excluded.config, set_at = excluded.set_at;
            """;
        cmd.Parameters.AddWithValue("$sz", sz);
        cmd.Parameters.AddWithValue("$config", config);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetLastTestConfigAsync(string sz, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT config FROM test_config WHERE sz = $sz;";
        cmd.Parameters.AddWithValue("$sz", sz);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task AddMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO maintenance (sz, from_at, until_at, reason)
            VALUES ($sz, $from, $until, $reason);
            """;
        cmd.Parameters.AddWithValue("$sz", window.Sz);
        cmd.Parameters.AddWithValue("$from", window.From.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$until", window.Until.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$reason", window.Reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceAsync(string sz,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await ReadMaintenanceAsync(conn, sz, ct);
    }

    private static async Task<IReadOnlyList<MaintenanceWindow>> ReadMaintenanceAsync(
        SqliteConnection conn, string sz, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT from_at, until_at, reason FROM maintenance WHERE sz = $sz ORDER BY from_at;";
        cmd.Parameters.AddWithValue("$sz", sz);
        var result = new List<MaintenanceWindow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new MaintenanceWindow(sz,
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.GetString(2)));
        }
        return result;
    }

    /// <summary>Слияние событий из журнала клиента: hub видит только смены boot-time при живом
    /// heartbeat, поэтому всё, что случилось до подключения агента, приходит отсюда. Дубли
    /// отсекаем по времени (±5 минут) — одно и то же событие hub мог уже записать сам.
    /// Возвращает добавленные события (не только их число): AgentHub пишет по ним отдельные
    /// строки в журнал СЗ для сна (бэклог п.140/222), не задваивая записи при переподключении.</summary>
    public async Task<IReadOnlyList<PowerEvent>> MergeJournalEventsAsync(PowerEventsReport report, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var added = new List<PowerEvent>();
        foreach (var evt in report.Events)
        {
            var at = evt.At.ToUnixTimeSeconds();
            await using var check = conn.CreateCommand();
            // Дубль ищем только среди событий, которые hub видел САМ (±5 минут — часы клиента
            // и hub расходятся), плюс точное совпадение с уже влитым журналом (повторный merge).
            // Сверка по всей таблице резала настоящие серии: пять вырубонов каждые 2 минуты —
            // самый показательный симптом — схлопывались до одного (бэклог п.106).
            check.CommandText = """
                SELECT COUNT(*) FROM reboots WHERE sz = $sz AND (
                    (source <> $journal AND ABS(at - $at) <= 300)
                    OR (source = $journal AND at = $at)
                );
                """;
            check.Parameters.AddWithValue("$sz", report.Sz);
            check.Parameters.AddWithValue("$at", at);
            check.Parameters.AddWithValue("$journal", RebootSource.Journal);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) continue;

            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO reboots (sz, at, prev_boot, new_boot, uptime_before, activity, kind, source, bugcheck, duration_seconds)
                VALUES ($sz, $at, NULL, NULL, $uptime, NULL, $kind, $source, $bugcheck, $duration);
                """;
            insert.Parameters.AddWithValue("$sz", report.Sz);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$kind", evt.Kind);
            insert.Parameters.AddWithValue("$source", RebootSource.Journal);
            insert.Parameters.AddWithValue("$bugcheck", evt.Bugcheck != 0 ? evt.Bugcheck : DBNull.Value);
            // Длительность сеанса от 6005 до 6008 (бэклог п.223) - раньше журнальные записи
            // всегда шли с NULL, и «Продержалась» в `szcli reboots` оставалась прочерком даже
            // когда агент это время уже знал.
            insert.Parameters.AddWithValue("$uptime",
                evt.UptimeBeforeSeconds.HasValue ? evt.UptimeBeforeSeconds.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$duration", (object?)evt.DurationSeconds ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
            added.Add(evt);
        }
        return added;
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
