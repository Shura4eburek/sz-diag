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
