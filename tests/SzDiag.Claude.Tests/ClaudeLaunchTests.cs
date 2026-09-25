using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class ClaudeLaunchTests
{
    private static ClaudeLaunch L(string? resume = null, string? configDir = "C:\\cfg")
        => new("C:\\bin\\claude.exe", "C:\\repo", configDir, resume, "вводная СЗ 161432", "C:\\run\\161432.mcp.json");

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Arguments_StreamJsonPermissionsMcp()
    {
        var a = L().Arguments();
        Assert.Equal(new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" }, a.Take(6));
        Assert.Equal("auto", After(a, "--permission-mode"));   // по умолчанию — как терминальный Claude
        Assert.Equal("mcp__desk__permission_prompt", After(a, "--permission-prompt-tool"));
        Assert.Equal("C:\\run\\161432.mcp.json", After(a, "--mcp-config"));
        Assert.Equal("вводная СЗ 161432", After(a, "--append-system-prompt"));
        Assert.DoesNotContain("--resume", a);
    }

    [Fact]
    public void Arguments_PeerToolsPreAllowed()
    {
        // Решение плана части 4: обмен между сессиями ничего не меняет, а лимиты держит Desk —
        // карточка разрешения на каждый вопрос соседу была бы шумом.
        Assert.Equal("mcp__desk__peers,mcp__desk__ask_peer", After(L().Arguments(), "--allowedTools"));
    }

    [Fact]
    public void OnlyDeskMcp_NoProfileConnectors()
    {
        // Бэклог п.266 (160176): коннекторы профиля (Supabase, Drive, IBKR…) грузились в каждую
        // сессию заявки, и кэш промпта между ходами терялся — «ОК» стоил $0.29.
        Assert.Contains("--strict-mcp-config", L().Arguments());
        Assert.Equal("false", L().ToStartInfo().Environment["ENABLE_CLAUDEAI_MCP_SERVERS"]);
    }

    [Fact]
    public void Arguments_Resume() => Assert.Equal("sid-1", After(L("sid-1").Arguments(), "--resume"));

    [Fact]
    public void ToStartInfo_SetsConfigDir()
    {
        // Профиль на боксе задаёт обёртка claude2.cmd, а не переменная пользователя: Desk из
        // Проводника без этого поднял бы claude с чужим профилем.
        var psi = L().ToStartInfo();
        Assert.Equal("C:\\cfg", psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("C:\\repo", psi.WorkingDirectory);
        Assert.Equal("C:\\bin\\claude.exe", psi.FileName);
        Assert.Equal(L().Arguments(), psi.ArgumentList);
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void Arguments_PermissionModeDefault_WhenAsked()
        => Assert.Equal("default", After((L() with { PermissionMode = "default" }).Arguments(), "--permission-mode"));

    [Fact]
    public void ToStartInfo_NoConfigDir_IsDefaultProfile_EvenIfParentHasOne()
    {
        // Профиль выбирается на сессию: null — профиль по умолчанию (~/.claude), а не «что было
        // у запустившего Desk» — иначе выбор «claude» из-под claude2 молча дал бы claude2.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", @"C:\чужой");
        try { Assert.False(L(configDir: null).ToStartInfo().Environment.ContainsKey("CLAUDE_CONFIG_DIR")); }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); }
    }

    [Fact]
    public void Profiles_DiscoveredByCredentials()
    {
        var home = Path.Combine(Path.GetTempPath(), "szhome-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (dir, creds) in new[] { (".claude", true), (".claude2", true), (".claude-tg-bridge", false), ("other", true) })
            {
                Directory.CreateDirectory(Path.Combine(home, dir));
                File.WriteAllText(Path.Combine(home, dir, creds ? ".credentials.json" : "settings.json"), "{}");
            }

            var found = ClaudeProfiles.Discover(home);

            Assert.Equal(new[] { "claude", "claude2" }, found.Select(p => p.Name));
            Assert.Null(found[0].ConfigDir);                                   // по умолчанию — без CLAUDE_CONFIG_DIR
            Assert.Equal(Path.Combine(home, ".claude2"), found[1].ConfigDir);
            Assert.Empty(ClaudeProfiles.Discover(Path.Combine(home, "нет")));
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void ToStartInfo_StripsInheritedSessionMarkers()
    {
        // Живая проверка: Desk, запущенный из сессии Claude Code, передавал детям её маркеры —
        // CLAUDE_CODE_CHILD_SESSION выключал запись транскрипта, а MESSAGING_SOCKET/TOKEN вели
        // в канал чужой сессии.
        Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "1");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN", "x");
        try
        {
            var env = L().ToStartInfo().Environment;
            Assert.All(ClaudeLaunch.InheritedSessionMarkers, name => Assert.False(env.ContainsKey(name), name));
            Assert.Equal("C:\\cfg", env["CLAUDE_CONFIG_DIR"]);   // профиль — не маркер, остаётся
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN", null);
        }
    }

    [Fact]
    public void ToStartInfo_StdinUtf8WithoutBom()
    {
        // BOM в начале stdin сломал бы разбор первой же строки у claude.
        var enc = L().ToStartInfo().StandardInputEncoding!;
        Assert.Equal(65001, enc.CodePage);
        Assert.Empty(enc.GetPreamble());
    }

    [Fact]
    public void UserMessage_Shape()
    {
        var m = JsonDocument.Parse(ClaudeInput.UserMessage("проверь \"SMART\"")).RootElement;
        Assert.Equal("user", m.GetProperty("type").GetString());
        Assert.Equal("user", m.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("проверь \"SMART\"", m.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Interrupt_Shape()
    {
        var m = JsonDocument.Parse(ClaudeInput.Interrupt("int-7")).RootElement;
        Assert.Equal("control_request", m.GetProperty("type").GetString());
        Assert.Equal("int-7", m.GetProperty("request_id").GetString());
        Assert.Equal("interrupt", m.GetProperty("request").GetProperty("subtype").GetString());
    }

    [Fact]
    public void Locator_FindsInPath_AndHonoursConfigured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "szclaude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "claude.exe");
            File.WriteAllText(exe, "");
            Assert.Equal(exe, ClaudeLocator.Resolve(null, $"C:\\нет{Path.PathSeparator}{dir}"));
            Assert.Equal(exe, ClaudeLocator.Resolve(exe, null));
            Assert.Null(ClaudeLocator.Resolve(Path.Combine(dir, "другой.exe"), dir));   // явный путь не подменяем PATH
            Assert.Null(ClaudeLocator.Resolve("", "C:\\нет"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
