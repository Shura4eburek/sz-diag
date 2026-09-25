using System.Text.Json;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class DeskClaudeHostTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szdesk-" + Guid.NewGuid().ToString("N"));
    private DeskClaudeHost _host = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var exe = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(exe, "");
        _host = await DeskClaudeHost.StartAsync(
            new DeskOptions { ClaudePath = exe, ClaudeWorkDir = _dir, ClaudeConfigDir = "C:\\cfg2" }, _dir);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void LaunchFor_WritesMcpConfig_PassesConfigDirAndResume()
    {
        var l = _host.LaunchFor("161432", "sid-1")!;
        Assert.Equal("sid-1", l.ResumeSessionId);
        Assert.Equal("C:\\cfg2", l.ConfigDir);
        Assert.Equal(_dir, l.WorkDir);
        Assert.Contains("161432", l.AppendSystemPrompt);
        Assert.StartsWith(Path.Combine(_dir, "run"), l.McpConfigPath);

        var desk = JsonDocument.Parse(File.ReadAllText(l.McpConfigPath)).RootElement
            .GetProperty("mcpServers").GetProperty("desk");
        Assert.EndsWith("/mcp/161432", desk.GetProperty("url").GetString());
        Assert.Equal(86_400_000, desk.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task NoClaude_LaunchNull()
    {
        await using var h = await DeskClaudeHost.StartAsync(
            new DeskOptions { ClaudePath = Path.Combine(_dir, "нет.exe") }, _dir);
        Assert.Null(h.LaunchFor("161432", null));
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
        var b = SzBriefing.For("161432");
        Assert.Contains("szcli", b);
        Assert.Contains("kb/СЗ/161432", b);
        Assert.Contains("161432", b.Split('\n')[0]);
    }

    [Fact]
    public void TerminalScript_SetsProfileWorkDirAndResume()
    {
        var s = TerminalLauncher.Script("C:\\bin\\claude.exe", "C:\\repo", "C:\\cfg2", "sid-1");
        Assert.StartsWith("@echo off", s);
        Assert.Contains("set \"CLAUDE_CONFIG_DIR=C:\\cfg2\"", s);
        Assert.Contains("cd /d \"C:\\repo\"", s);
        Assert.Contains("\"C:\\bin\\claude.exe\" --resume sid-1", s);
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", TerminalLauncher.Script("C:\\bin\\claude.exe", "C:\\repo", null, "sid-1"));
    }
}
