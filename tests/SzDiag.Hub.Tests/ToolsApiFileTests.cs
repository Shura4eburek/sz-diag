namespace SzDiag.Hub.Tests;

public class ToolsApiFileTests
{
    [Fact]
    public void OpenToolFile_IsAsync()
    {
        // Синхронный FileStream гоняет ReadAsync через пул потоков — на 300 МБ OCCT это
        // лишняя нагрузка ровно на тот пул, который у hub уже захлёбывался (бэклог п.50).
        var path = Path.GetTempFileName();
        try
        {
            using var s = ToolsApi.OpenToolFile(path);
            Assert.True(Assert.IsType<FileStream>(s).IsAsync);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CountingReadStream_CountsArrayReadAsync()
    {
        long counted = 0;
        await using var s = new CountingReadStream(new MemoryStream(new byte[1000]), n => counted += n);
        var buf = new byte[400];
        while (await s.ReadAsync(buf, 0, buf.Length) > 0) { }
        Assert.Equal(1000, counted);
    }
}
