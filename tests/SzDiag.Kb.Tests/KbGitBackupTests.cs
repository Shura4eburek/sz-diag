using System.Diagnostics;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbGitBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-git-{Guid.NewGuid():N}");
    private readonly string _vault;
    private readonly string _remote;

    public KbGitBackupTests()
    {
        _vault = Path.Combine(_root, "vault");
        _remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_vault);
        Directory.CreateDirectory(_remote);

        Git(_remote, "init --bare --initial-branch=main");
        Git(_vault, "init --initial-branch=main");
        // Без user.name/user.email git отказывается коммитить.
        Git(_vault, "config user.email test@szdiag.local");
        Git(_vault, "config user.name SzDiagTest");
        Git(_vault, $"remote add origin \"{_remote}\"");

        // Стартовый коммит: чтобы у ветки main была история и push имел что толкать.
        File.WriteAllText(Path.Combine(_vault, "README.md"), "kb");
        Git(_vault, "add -A");
        Git(_vault, "commit -m init");
        Git(_vault, "push origin main");
    }

    private KbGitBackup NewBackup(string? vault = null, string remoteName = "origin")
        => new(vault ?? _vault, remoteName, "main", TimeSpan.FromMinutes(1));

    [Fact]
    public async Task RunAsync_NoChanges_ReturnsNoChanges()
    {
        var result = await NewBackup().RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, result.ChangedFiles);
        Assert.Equal(1, CountRemoteCommits());
    }

    [Fact]
    public async Task RunAsync_NewFile_PushesToRemote()
    {
        File.WriteAllText(Path.Combine(_vault, "нотатка.md"), "текст");

        var result = await NewBackup().RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.Pushed, result.Outcome);
        Assert.Equal(1, result.ChangedFiles);
        Assert.Equal(2, CountRemoteCommits());
        Assert.Contains("нотатка.md", GitOut(_remote, "ls-tree --name-only -r main"));
    }

    [Fact]
    public async Task RunAsync_DeletedFile_IsAlsoBackedUp()
    {
        File.Delete(Path.Combine(_vault, "README.md"));

        var result = await NewBackup().RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.Pushed, result.Outcome);
        Assert.DoesNotContain("README.md", GitOut(_remote, "ls-tree --name-only -r main"));
    }

    [Fact]
    public async Task RunAsync_CommitSubject_HasCyrillicAndNoBom()
    {
        File.WriteAllText(Path.Combine(_vault, "нотатка.md"), "текст");

        await NewBackup().RunAsync(CancellationToken.None);

        var subject = GitOut(_vault, "log -1 --pretty=%s").Trim();
        Assert.StartsWith("kb: автосохранение", subject);
        // BOM (U+FEFF) — escape-последовательностью: голый символ в исходнике невидим.
        Assert.DoesNotContain('\uFEFF', subject);
    }

    [Fact]
    public async Task RunAsync_UnreachableRemote_CommitsLocallyAndReports()
    {
        Git(_vault, $"remote add broken \"{Path.Combine(_root, "нет-такого.git")}\"");
        File.WriteAllText(Path.Combine(_vault, "нотатка.md"), "текст");

        var result = await NewBackup(remoteName: "broken").RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.CommittedNotPushed, result.Outcome);
        Assert.Equal(2, CountLocalCommits());
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task RunAsync_NotAGitRepo_ReturnsFailedWithoutThrowing()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        var result = await NewBackup(vault: plain).RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.Failed, result.Outcome);
        Assert.Contains(plain, result.Message);
    }

    private int CountRemoteCommits()
        => int.Parse(GitOut(_remote, "rev-list --count main").Trim());

    private int CountLocalCommits()
        => int.Parse(GitOut(_vault, "rev-list --count main").Trim());

    private static void Git(string cwd, string args) => GitOut(cwd, args);

    private static string GitOut(string cwd, string args)
    {
        // core.quotepath=false: иначе git отдаёт кириллические пути октальными
        // escape-последовательностями ("\320\275…") и проверки имён не сходятся.
        var psi = new ProcessStartInfo("git", "-c core.quotepath=false " + args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args}: {stderr}{stdout}");
        return stdout;
    }

    public void Dispose()
    {
        // .git держит файлы read-only — снимаем атрибут, иначе Delete падает.
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
