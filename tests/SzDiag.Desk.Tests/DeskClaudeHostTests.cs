using System.Text.Json;
using SzDiag.Claude;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class DeskClaudeHostTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szdesk-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_dir, "home");
    private string Work => Path.Combine(_dir, "work");
    private string Kb => Path.Combine(_dir, "kb");
    private DeskClaudeHost _host = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        // Два профиля, как на боксе: ~/.claude и ~/.claude2 — с входом; сосед без входа — не профиль.
        foreach (var (dir, creds) in new[] { (".claude", true), (".claude2", true), (".claude-tg-bridge", false) })
        {
            Directory.CreateDirectory(Path.Combine(Home, dir));
            File.WriteAllText(Path.Combine(Home, dir, creds ? ".credentials.json" : "settings.json"), "{}");
        }
        var exe = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(exe, "");
        _host = await DeskClaudeHost.StartAsync(
            new DeskOptions { ClaudePath = exe, ClaudeWorkDir = _dir, ClaudeHome = Home, SessionWorkDir = Work, KbRoot = Kb }, _dir);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SessionRecord R(string? profile, string? sid = "sid-1")
        => new("161432", sid, DateTimeOffset.UnixEpoch, false, profile);

    [Fact]
    public void Profiles_Discovered()
        => Assert.Equal(new[] { "claude", "claude2" }, _host.Profiles.Select(p => p.Name));

    [Fact]
    public void LaunchFor_ProfileConfigDir_Resume_McpConfig_AutoMode()
    {
        var l = _host.LaunchFor(R("claude2"))!;
        Assert.Equal("sid-1", l.ResumeSessionId);
        Assert.Equal(Path.Combine(Home, ".claude2"), l.ConfigDir);
        Assert.Equal("auto", l.PermissionMode);
        Assert.Equal(_dir, l.WorkDir);
        Assert.Contains("161432", l.AppendSystemPrompt);
        Assert.StartsWith(Path.Combine(_dir, "run"), l.McpConfigPath);

        var desk = JsonDocument.Parse(File.ReadAllText(l.McpConfigPath)).RootElement
            .GetProperty("mcpServers").GetProperty("desk");
        Assert.EndsWith("/mcp/161432", desk.GetProperty("url").GetString());
        Assert.Equal(86_400_000, desk.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public void LaunchFor_LegacyRecord_RepoRoot_NoAddDirs()
    {
        // Разговоры, начатые до лёгкого CLAUDE.md, живут в каталоге репозитория: --resume иначе не найдёт.
        var l = _host.LaunchFor(R("claude2"))!;
        Assert.Equal(_dir, l.WorkDir);
        Assert.Null(l.AddDirs);
    }

    [Fact]
    public void LaunchFor_NewSession_LeanWorkspace_WithKbAndRepo()
    {
        var l = _host.LaunchFor(R("claude2") with { WorkDir = _host.SessionWorkDir })!;
        Assert.Equal(Work, l.WorkDir);
        Assert.Equal(new[] { Kb, _dir }, l.AddDirs);
    }

    [Fact]
    public void Start_WritesLeanClaudeMd_WithPaths()
    {
        var md = File.ReadAllText(Path.Combine(Work, "CLAUDE.md"));
        Assert.Contains(Kb, md);
        Assert.Contains(_dir, md);
        Assert.DoesNotContain("{{", md);
        Assert.Contains("lastHeartbeat", md);
    }

    [Fact]
    public void DefaultSessionWorkDir_OutsideRepo()
    {
        // Claude Code поднимается по родительским папкам за CLAUDE.md — каталог внутри репозитория
        // снова подтянул бы большой CLAUDE.md разработчика.
        var d = new DeskOptions().ResolveSessionWorkDir();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), d);
        Assert.EndsWith(Path.Combine("SzDiag", "desk-work"), d);
    }

    [Fact]
    public void LaunchFor_NoProfile_DefaultClaude()
        => Assert.Null(_host.LaunchFor(R(null))!.ConfigDir);

    [Fact]
    public void LaunchFor_VanishedProfile_Null()
    {
        // Профиль закреплён за разговором: подменить его другим — значит --resume не найдёт сессию.
        Assert.Null(_host.LaunchFor(R("claude3")));
    }

    [Fact]
    public async Task PermissionMode_FromOptions()
    {
        await using var h = await DeskClaudeHost.StartAsync(new DeskOptions
        {
            ClaudePath = Path.Combine(_dir, "claude.exe"), ClaudeHome = Home, PermissionMode = "default",
        }, _dir);
        Assert.Equal("default", h.LaunchFor(R(null))!.PermissionMode);
    }

    [Fact]
    public async Task NoClaude_LaunchNull()
    {
        await using var h = await DeskClaudeHost.StartAsync(
            new DeskOptions { ClaudePath = Path.Combine(_dir, "нет.exe"), ClaudeHome = Home }, _dir);
        Assert.Null(h.LaunchFor(R(null)));
    }

    [Fact]
    public void FindRepoRoot_WalksUpToSolution()
    {
        var nested = Path.Combine(_dir, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_dir, "SzDiag.sln"), "");
        Assert.Equal(_dir, DeskClaudeHost.FindRepoRoot(nested));
    }

    [Fact]
    public void Briefing_BindsSessionToSz()
    {
        var b = SzBriefing.For("161432", @"C:\repo\dist\host\szcli.cmd");
        Assert.Contains(@"C:\repo\dist\host\szcli.cmd", b);
        Assert.Contains("kb/СЗ/161432", b);
        Assert.Contains("161432", b.Split('\n')[0]);
    }

    [Fact]
    public void LaunchFor_BriefingNamesSzcliFromDist()
    {
        // Живая проверка: szcli на боксе не в PATH, и сессия тратила ход на «not recognized».
        var szcli = Path.Combine(_dir, "dist", "host", "szcli.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(szcli)!);
        File.WriteAllText(szcli, "");
        Assert.Equal(szcli, DeskClaudeHost.FindSzcli(Path.Combine(_dir, "bin"), _dir));
        Assert.Contains(szcli, _host.LaunchFor(R(null))!.AppendSystemPrompt);
    }

    [Fact]
    public void FindSzcli_NextToDeskInDist_ThenRepo_ThenNull()
    {
        var deskDir = Path.Combine(_dir, "d", "host", "desk");
        Directory.CreateDirectory(deskDir);
        var sibling = Path.Combine(_dir, "d", "host", "szcli.cmd");
        File.WriteAllText(sibling, "");
        Assert.Equal(sibling, DeskClaudeHost.FindSzcli(deskDir, Path.Combine(_dir, "нет")));
        Assert.Null(DeskClaudeHost.FindSzcli(Path.Combine(_dir, "x"), Path.Combine(_dir, "нет")));
    }

    [Fact]
    public void TerminalScript_SetsProfileWorkDirAndResume()
    {
        var s = TerminalLauncher.Script(@"C:\bin\claude.exe", @"C:\repo", @"C:\cfg2", "sid-1");
        Assert.StartsWith("@echo off", s);
        Assert.Contains(@"set ""CLAUDE_CONFIG_DIR=C:\cfg2""", s);
        Assert.Contains(@"cd /d ""C:\repo""", s);
        Assert.Contains(@"""C:\bin\claude.exe"" --resume sid-1", s);
        // Профиль по умолчанию — переменная снимается, а не наследуется от запустившего Desk.
        Assert.Contains(@"set ""CLAUDE_CONFIG_DIR=""", TerminalLauncher.Script(@"C:\bin\claude.exe", @"C:\repo", null, "sid-1"));
        // Маркеры сессии, из которой запущен Desk, в терминал не протекают.
        Assert.All(ClaudeLaunch.InheritedSessionMarkers, name => Assert.Contains($"set \"{name}=\"", s));
    }
}
