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
                source        TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reboots_sz ON reboots(sz);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Миграция для баз, заведённых до появления классификации (бэклог п.93): у старых
        // записей kind останется NULL и будет читаться как «неизвестно», а не как «кнопка».
        foreach (var column in new[] { "kind TEXT NULL", "source TEXT NULL" })
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
            INSERT INTO reboots (sz, at, prev_boot, new_boot, uptime_before, activity, kind, source)
            VALUES ($sz, $at, $prev, $new, $uptime, $activity, $kind, $source);
            """;
        cmd.Parameters.AddWithValue("$kind", (object?)evt.Kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", evt.Source);
        cmd.Parameters.AddWithValue("$sz", evt.Sz);
        cmd.Parameters.AddWithValue("$at", evt.At.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$prev", (object?)evt.PreviousBootTime?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$new", (object?)evt.NewBootTime?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uptime", (object?)evt.UptimeBeforeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity", (object?)evt.ActivityBefore ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RebootTimeline> GetRebootsAsync(string sz, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT at, prev_boot, new_boot, uptime_before, activity, kind, source
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
                reader.IsDBNull(6) ? RebootSource.Heartbeat : reader.GetString(6)));
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

        return new RebootTimeline(sz, events, maxUptime, watchingSince);
    }

    /// <summary>Слияние событий из журнала клиента: hub видит только смены boot-time при живом
    /// heartbeat, поэтому всё, что случилось до подключения агента, приходит отсюда. Дубли
    /// отсекаем по времени (±5 минут) — одно и то же событие hub мог уже записать сам.</summary>
    public async Task<int> MergeJournalEventsAsync(PowerEventsReport report, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var added = 0;
        foreach (var evt in report.Events)
        {
            var at = evt.At.ToUnixTimeSeconds();
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM reboots WHERE sz = $sz AND ABS(at - $at) <= 300;";
            check.Parameters.AddWithValue("$sz", report.Sz);
            check.Parameters.AddWithValue("$at", at);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) continue;

            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO reboots (sz, at, prev_boot, new_boot, uptime_before, activity, kind, source)
                VALUES ($sz, $at, NULL, NULL, NULL, NULL, $kind, $source);
                """;
            insert.Parameters.AddWithValue("$sz", report.Sz);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$kind", evt.Kind);
            insert.Parameters.AddWithValue("$source", RebootSource.Journal);
            await insert.ExecuteNonQueryAsync(ct);
            added++;
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
