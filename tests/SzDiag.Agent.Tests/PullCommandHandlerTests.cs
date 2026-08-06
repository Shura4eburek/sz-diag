using System.Security.Cryptography;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class PullCommandHandlerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szpull-{Guid.NewGuid():N}");
    private readonly List<PullChunk> _chunks = new();

    public PullCommandHandlerTests() => Directory.CreateDirectory(_dir);

    private PullCommandHandler Handler(int chunkBytes = 64)
        => new((chunk, _) => { _chunks.Add(chunk); return Task.CompletedTask; }, chunkBytes);

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private byte[] Assembled(string fullPath)
        => _chunks.Where(c => c.FullPath == fullPath).OrderBy(c => c.Index)
            .SelectMany(c => c.Data).ToArray();

    [Fact]
    public async Task Handle_SingleFile_SendsChunksAndReportsSha()
    {
        var content = Bytes(200);   // при чанке 64 это 4 куска
        var path = WriteFile("mini.dmp", content);

        var result = await Handler().HandleAsync(new PullRequest("160705", "req-1", path, 1024 * 1024));

        var file = Assert.Single(result.Files);
        Assert.False(file.Skipped);
        Assert.Equal(200, file.Size);
        Assert.Equal(Sha(content), file.Sha256);
        Assert.Equal(4, _chunks.Count);
        Assert.True(_chunks.Last().Last);
        Assert.Equal(content, Assembled(path));   // файл собирается байт-в-байт
    }

    [Fact]
    public async Task Handle_Mask_PicksMatchingFilesOnly()
    {
        WriteFile("a.dmp", Bytes(10));
        WriteFile("b.dmp", Bytes(10));
        WriteFile("c.log", Bytes(10));

        var result = await Handler().HandleAsync(
            new PullRequest("160705", "req-2", Path.Combine(_dir, "*.dmp"), 1024 * 1024));

        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, f => Assert.EndsWith(".dmp", f.Name));
    }

    [Fact]
    public async Task Handle_FileOverLimit_SkippedWithSizeInReason()
    {
        // Рядом с минидампами лежат live-дампы на гигабайты — тянуть их нельзя,
        // но размер показать надо (бэклог п.37).
        var path = WriteFile("huge.dmp", Bytes(5000));

        var result = await Handler().HandleAsync(new PullRequest("160705", "req-3", path, 1000));

        var file = Assert.Single(result.Files);
        Assert.True(file.Skipped);
        Assert.Equal(5000, file.Size);
        Assert.Contains("больше лимита", file.SkipReason);
        Assert.Empty(_chunks);   // ни одного байта не потащили
    }

    [Fact]
    public async Task Handle_MissingPath_ReturnsErrorNotException()
    {
        var result = await Handler().HandleAsync(
            new PullRequest("160705", "req-4", Path.Combine(_dir, "нет-такого.dmp"), 1024));

        Assert.Empty(result.Files);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Handle_EmptyFile_StillProducesOneChunk()
    {
        // Иначе на хосте не появилось бы файла вовсе, и это выглядело бы как сбой.
        var path = WriteFile("empty.log", Array.Empty<byte>());

        var result = await Handler().HandleAsync(new PullRequest("160705", "req-5", path, 1024));

        Assert.False(Assert.Single(result.Files).Skipped);
        Assert.Single(_chunks);
        Assert.True(_chunks[0].Last);
        Assert.Empty(_chunks[0].Data);
    }

    [Fact]
    public async Task Handle_FileOpenForWriting_StillReadable()
    {
        // CSV сенсоров и Log.txt теста пишутся прямо во время забора — останавливать
        // прогон ради выгрузки нельзя.
        var path = WriteFile("sensors.csv", Bytes(100));
        using var busy = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = await Handler().HandleAsync(new PullRequest("160705", "req-6", path, 1024 * 1024));

        Assert.False(Assert.Single(result.Files).Skipped);
    }

    [Fact]
    public void Resolve_Directory_ReturnsAllFilesWithoutRecursion()
    {
        WriteFile("one.txt", Bytes(3));
        WriteFile("two.txt", Bytes(3));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "sub", "deep.txt"), Bytes(3));

        var found = PullCommandHandler.Resolve(_dir);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Resolve_Recurse_FindsFilesInSubfolders()
    {
        // Ровно устройство C:\Windows\LiveKernelReports: в корне гигантский файл, всё нужное —
        // в подпапках WATCHDOG* (бэклог п.75).
        WriteFile("root.dmp", Bytes(3));
        Directory.CreateDirectory(Path.Combine(_dir, "WATCHDOG4400"));
        File.WriteAllBytes(Path.Combine(_dir, "WATCHDOG4400", "a.dmp"), Bytes(3));
        Directory.CreateDirectory(Path.Combine(_dir, "WATCHDOG4401"));
        File.WriteAllBytes(Path.Combine(_dir, "WATCHDOG4401", "b.dmp"), Bytes(3));

        var found = PullCommandHandler.Resolve(_dir, recurse: true);

        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void Resolve_RecurseWithMask_FiltersByMaskInSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "WATCHDOG"));
        File.WriteAllBytes(Path.Combine(_dir, "WATCHDOG", "a.dmp"), Bytes(3));
        File.WriteAllBytes(Path.Combine(_dir, "WATCHDOG", "notes.txt"), Bytes(3));

        var found = PullCommandHandler.Resolve(Path.Combine(_dir, "*.dmp"), recurse: true);

        Assert.Single(found);
        Assert.EndsWith("a.dmp", found[0]);
    }

    [Fact]
    public async Task Handle_OverLimit_MarksSkipAsLimitNotError()
    {
        // Пропуск по лимиту — штатное поведение, ради которого лимит и задавался: CLI не должен
        // считать это провалом команды (бэклог п.75).
        var path = WriteFile("huge.dmp", Bytes(2048));

        var result = await Handler().HandleAsync(new PullRequest("161312", "req-lim", path, 1024));

        var file = Assert.Single(result.Files);
        Assert.True(file.Skipped);
        Assert.True(file.OverLimit);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
