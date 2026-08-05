using System.Diagnostics;
using System.Text;

namespace SzDiag.Kb;

/// <summary>
/// Коммитит изменения vault'а и пушит в remote. Дёргает системный git.exe:
/// он уже стоит на боксе и сам берёт креды из Windows Credential Manager.
/// </summary>
public sealed class KbGitBackup : IKbBackup
{
    private readonly string _vaultRoot;
    private readonly string _remote;
    private readonly string _branch;
    private readonly TimeSpan _commandTimeout;

    public KbGitBackup(string vaultRoot, string remote, string branch, TimeSpan commandTimeout)
    {
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _remote = remote;
        _branch = branch;
        _commandTimeout = commandTimeout;
    }

    public async Task<KbBackupResult> RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(_vaultRoot, ".git")))
            return new KbBackupResult(KbBackupOutcome.Failed, 0, $"не git-репозиторий: {_vaultRoot}");

        try
        {
            var add = await RunGitAsync("add -A", ct);
            if (add.ExitCode != 0)
                return new KbBackupResult(KbBackupOutcome.Failed, 0, $"git add: {First(add.Error, add.Output)}");

            var status = await RunGitAsync("status --porcelain", ct);
            if (status.ExitCode != 0)
                return new KbBackupResult(KbBackupOutcome.Failed, 0, $"git status: {First(status.Error, status.Output)}");

            var changed = status.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
            if (changed == 0)
                return new KbBackupResult(KbBackupOutcome.NoChanges, 0, "изменений нет");

            var commit = await CommitAsync(changed, ct);
            if (commit.ExitCode != 0)
            {
                // Сюда же прилетает отлуп pre-commit hook'а (жирный файл в vault).
                return new KbBackupResult(KbBackupOutcome.Failed, 0,
                    $"git commit: {First(commit.Error, commit.Output)}");
            }

            var push = await RunGitAsync($"push {_remote} {_branch}", ct);
            if (push.ExitCode != 0)
            {
                return new KbBackupResult(KbBackupOutcome.CommittedNotPushed, changed,
                    First(push.Error, push.Output));
            }

            return new KbBackupResult(KbBackupOutcome.Pushed, changed, "выгружено");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new KbBackupResult(KbBackupOutcome.Failed, 0, ex.Message);
        }
    }

    private async Task<GitRun> CommitAsync(int changed, CancellationToken ct)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
        var msgFile = Path.Combine(Path.GetTempPath(), $"kb-backup-msg-{Guid.NewGuid():N}.txt");
        // Строго UTF-8 БЕЗ BOM: иначе git утаскивает BOM в первую строку заголовка коммита.
        await File.WriteAllTextAsync(
            msgFile, $"kb: автосохранение {stamp} ({changed} файл(ов))",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        try
        {
            return await RunGitAsync($"commit -F \"{msgFile}\"", ct);
        }
        finally
        {
            try { File.Delete(msgFile); } catch (IOException) { }
        }
    }

    private async Task<GitRun> RunGitAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _vaultRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"не удалось запустить git {args}");

        // Потоки читаем параллельно с ожиданием: заполненный буфер pipe вешает процесс.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_commandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Виснет сеть — убиваем git, hub не должен ждать его вечно.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new GitRun(-1, "", $"git {args}: таймаут {_commandTimeout}");
        }

        return new GitRun(process.ExitCode, await stdout, await stderr);
    }

    private static string First(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "без деталей" : line;
    }

    private readonly record struct GitRun(int ExitCode, string Output, string Error);
}
