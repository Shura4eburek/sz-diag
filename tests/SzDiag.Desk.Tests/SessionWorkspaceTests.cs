using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class SessionWorkspaceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "desk-work-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private string Rendered()
    {
        SessionWorkspace.Write(_dir, @"C:\repo", @"C:\kb", @"C:\dist\host\szcli.cmd");
        return File.ReadAllText(Path.Combine(_dir, "CLAUDE.md"));
    }

    [Fact]
    public void Бриф_без_управляющих_символов()
    {
        // Путь `{{REPO}}\docs\teleauto-rest-api.md` однажды уехал в бриф с табуляцией вместо `\t`:
        // сессия получила битую ссылку на документ. Текст брифа — только печатные символы и переводы строк.
        var text = Rendered();
        var bad = text.Where(c => char.IsControl(c) && c is not '\n' and not '\r').ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Бриф_подставляет_пути_и_называет_REST_команды()
    {
        var text = Rendered();

        Assert.DoesNotContain("{{", text);
        Assert.Contains(@"C:\repo\docs\teleauto-rest-api.md", text);
        Assert.Contains("sz get", text);
        Assert.Contains("sz orders", text);
        Assert.Contains("ключ=значение", text);
    }
}
