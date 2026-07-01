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
