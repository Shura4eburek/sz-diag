using System.Collections.Concurrent;
using System.Security.Cryptography;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Забор файлов с клиента: шлёт агенту <see cref="PullRequest"/>, принимает куски
/// (<see cref="PullChunk"/>) и складывает их в файлы на хосте, а по <see cref="PullResult"/>
/// будит ожидающий HTTP-запрос CLI.
///
/// Файлы кладутся **не в vault**: дампы и CSV в git-репозитории базы знаний раздувают
/// историю необратимо (правило из CLAUDE.md). Каталог — <c>Hub.PullRoot</c>.</summary>
public sealed class PullCoordinator
{
    private sealed class Session
    {
        public required string Sz { get; init; }
        public required string Dir { get; init; }
        public TaskCompletionSource<PullResult> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Исходный путь на клиенте → куда пишем на хосте.</summary>
        public ConcurrentDictionary<string, string> Saved { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, FileStream> Streams { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;
    private readonly string _root;
    private readonly int _timeoutSeconds;
    private readonly ConcurrentDictionary<string, Session> _pending = new();

    public PullCoordinator(SessionRegistry registry, IAgentCommandSender sender, string root,
        int timeoutSeconds = PullLimits.TimeoutSeconds)
    {
        _registry = registry;
        _sender = sender;
        _root = root;
        _timeoutSeconds = timeoutSeconds;
    }

    public int PendingCount => _pending.Count;

    /// <summary>Забрать файлы с клиента. null — СЗ не онлайн.</summary>
    /// <exception cref="TimeoutException">Агент не завершил забор в отведённое время.</exception>
    public async Task<PullResponse?> PullAsync(string sz, string path, long? maxBytes = null,
        bool recurse = false, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return null;

        var requestId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(ResolveRoot(), sz, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var session = new Session { Sz = sz, Dir = dir };
        _pending[requestId] = session;
        try
        {
            Directory.CreateDirectory(dir);
            await _sender.SendPullAsync(connId,
                new PullRequest(sz, requestId, path, maxBytes ?? PullLimits.DefaultMaxBytes, recurse), ct);

            var wait = TimeSpan.FromSeconds(_timeoutSeconds);
            var done = await Task.WhenAny(session.Done.Task, Task.Delay(wait, ct));
            if (done != session.Done.Task)
                throw new TimeoutException($"агент СЗ {sz} не завершил забор за {wait.TotalSeconds:N0} с");

            var result = await session.Done.Task;
            return Materialize(session, result);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            CloseStreams(session);
        }
    }

    /// <summary>Агент прислал кусок файла — дописываем на диск. Чанк с неизвестным RequestId
    /// (запрос уже истёк) игнорируем, иначе на хосте копились бы файлы-сироты.</summary>
    public bool AcceptChunk(PullChunk chunk)
    {
        if (!_pending.TryGetValue(chunk.RequestId, out var session)) return false;

        var stream = session.Streams.GetOrAdd(chunk.FullPath, source =>
        {
            var target = UniqueTargetPath(session.Dir, Path.GetFileName(source));
            session.Saved[source] = target;
            return new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
        });

        stream.Write(chunk.Data, 0, chunk.Data.Length);
        if (chunk.Last) stream.Flush();
        return true;
    }

    /// <summary>Агент отчитался об окончании — будим ожидающий запрос.</summary>
    public bool Complete(PullResult result)
        => _pending.TryGetValue(result.RequestId, out var session) && session.Done.TrySetResult(result);

    /// <summary>Сверяет принятое с тем, что обещал агент: размер и sha256. Расхождение —
    /// явная ошибка в ответе, а не тихо битый файл на диске.</summary>
    private static PullResponse Materialize(Session session, PullResult result)
    {
        CloseStreams(session);

        var files = new List<PullSavedFile>();
        foreach (var f in result.Files)
        {
            if (f.Skipped)
            {
                files.Add(new PullSavedFile(f.Name, f.FullPath, f.Size, f.Sha256, null, true, f.SkipReason,
                    f.OverLimit));
                continue;
            }

            if (!session.Saved.TryGetValue(f.FullPath, out var target) || !File.Exists(target))
            {
                files.Add(new PullSavedFile(f.Name, f.FullPath, f.Size, f.Sha256, null, true,
                    "агент отчитался о файле, но данные не пришли"));
                continue;
            }

            var actualSize = new FileInfo(target).Length;
            var actualSha = Sha256Of(target);
            if (actualSize != f.Size || !actualSha.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new PullSavedFile(f.Name, f.FullPath, actualSize, actualSha, target, true,
                    $"файл побился при передаче (ожидалось {f.Size} байт / {f.Sha256})"));
                continue;
            }

            files.Add(new PullSavedFile(f.Name, f.FullPath, actualSize, actualSha, target));
        }

        return new PullResponse(files, result.Error);
    }

    private static void CloseStreams(Session session)
    {
        foreach (var key in session.Streams.Keys)
        {
            if (session.Streams.TryRemove(key, out var s))
            {
                try { s.Flush(); s.Dispose(); } catch { /* уже закрыт */ }
            }
        }
    }

    private static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Два файла с одинаковым именем из разных папок клиента не должны затирать
    /// друг друга.</summary>
    private static string UniqueTargetPath(string dir, string name)
    {
        var target = Path.Combine(dir, name);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private string ResolveRoot()
        => Path.IsPathRooted(_root) ? _root : Path.Combine(AppContext.BaseDirectory, _root);
}
