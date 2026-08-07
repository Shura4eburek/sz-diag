using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

/// <summary>В заметке СЗ 160467 показывался вывод по 159794: `висновок.md` в своей папке
/// отсутствовал, а Obsidian по короткой ссылке подставил первый попавшийся файл с тем же
/// именем (бэклог п.11).</summary>
public class KbDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-doctor-{Guid.NewGuid():N}");

    private string Sz(string sz) => Path.Combine(_root, "СЗ", sz);

    private void WriteSz(string sz, params (string Name, string Text)[] files)
    {
        Directory.CreateDirectory(Sz(sz));
        foreach (var (name, text) in files)
            File.WriteAllText(Path.Combine(Sz(sz), name + ".md"), text);
    }

    private static (string, string)[] FullSet(string sz) => new[]
    {
        ($"{sz}", $"# СЗ {sz}\n\n![[висновок]]\n"),
        ("запит", "x"), ("діагностика", "x"), ("дії", "x"), ("висновок", "y"),
    };

    [Fact]
    public void MissingSummary_IsAnError_BecauseEmbedWillPickSomeoneElses()
    {
        WriteSz("159794", FullSet("159794"));
        WriteSz("160467",
            ("160467", "# СЗ 160467\n\n![[висновок]]\n"),
            ("запит", "x"), ("діагностика", "x"), ("дії", "x"));

        var issues = KbDoctor.Check(_root);

        Assert.Contains(issues, i => i.IsError && i.Where == "160467" && i.What.Contains("висновок"));
        Assert.Contains(issues, i => i.IsError && i.What.Contains("подставит первый попавшийся"));
    }

    [Fact]
    public void HealthyVault_HasNoErrors()
    {
        WriteSz("160306", FullSet("160306"));

        var issues = KbDoctor.Check(_root);

        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void LinkToNowhere_IsWarning_NotError()
    {
        WriteSz("160306", FullSet("160306"));
        File.WriteAllText(Path.Combine(Sz("160306"), "діагностика.md"),
            "див. [[Симптоми/яких-немає]]\n");

        var issues = KbDoctor.Check(_root);

        Assert.Contains(issues, i => !i.IsError && i.What.Contains("в никуда"));
    }

    [Fact]
    public void ExistingEntityNote_IsNotReportedAsBrokenLink()
    {
        WriteSz("160306", FullSet("160306"));
        Directory.CreateDirectory(Path.Combine(_root, "Симптоми"));
        File.WriteAllText(Path.Combine(_root, "Симптоми", "випадкові перезавантаження.md"), "x");
        File.WriteAllText(Path.Combine(Sz("160306"), "діагностика.md"),
            "патерн: [[випадкові перезавантаження]]\n");

        var issues = KbDoctor.Check(_root);

        Assert.DoesNotContain(issues, i => i.What.Contains("випадкові перезавантаження"));
    }

    [Fact]
    public void EmptyVault_IsFine()
        => Assert.Empty(KbDoctor.Check(_root));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
