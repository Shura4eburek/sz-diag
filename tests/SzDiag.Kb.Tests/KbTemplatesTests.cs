using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

/// <summary>Ответ на жалобу собирался с нуля, и формат переделывали трижды (бэклог п.84).</summary>
public class KbTemplatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-tpl-{Guid.NewGuid():N}");

    [Fact]
    public void Scaffolder_CreatesComplaintTemplate()
    {
        new KnowledgeBaseScaffolder(_root).EnsureSkeleton("160306");

        var path = Path.Combine(_root, KbTemplates.FolderName, KbTemplates.ComplaintReplyFile);
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("ЩО ЗРОБЛЕНО", text);
        Assert.Contains("ПОЗИЦІЯ СЕРВІСУ", text);
    }

    [Fact]
    public void Template_CarriesFormatRulesLearnedTheHardWay()
    {
        var text = KbTemplates.ComplaintReply;

        Assert.Contains("адресат — колл-центр", text);
        Assert.Contains("plain text", text);
        Assert.Contains("ОДНИМ рядком", text);
        Assert.Contains("szcli reboots", text);   // не писать того, что опровергается журналами
    }

    [Fact]
    public void ExistingTemplate_IsNotOverwritten()
    {
        var scaffolder = new KnowledgeBaseScaffolder(_root);
        scaffolder.EnsureTemplates();
        var path = Path.Combine(_root, KbTemplates.FolderName, KbTemplates.ComplaintReplyFile);
        File.WriteAllText(path, "правленный руками шаблон");

        scaffolder.EnsureTemplates();

        Assert.Equal("правленный руками шаблон", File.ReadAllText(path));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
