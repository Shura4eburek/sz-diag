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

    [Fact]
    public void TemplatePlaceholders_AreClassifiedAsTemplate_NotWarnings()
    {
        // Регрессия (бэклог п.111): на боевом vault 38 из 40 «внимание» были ссылками,
        // которые сам hub кладёт в скелет ([[report]], [[симптом]]) — настоящие находки
        // тонули в этом списке.
        WriteSz("160306", FullSet("160306"));
        File.WriteAllText(Path.Combine(Sz("160306"), "висновок.md"),
            "- 🔗 сирий прогін: [[report]]\n**Патерн:** [[симптом]]\n");

        var issues = KbDoctor.Check(_root);

        Assert.DoesNotContain(issues, i => i.Severity == "warn" && i.What.Contains("[[report]]"));
        Assert.DoesNotContain(issues, i => i.Severity == "warn" && i.What.Contains("[[симптом]]"));
        Assert.Equal(2, issues.Count(i => i.Severity == "template"));
    }

    [Fact]
    public void DanglingEntityLinksInHomeNote_AreTemplate_RealTyposElsewhereStayWarnings()
    {
        // Ссылки на ещё не заведённые сущности из <sz>.md ([[номер заказа]], [[модель ПК]])
        // создаст kb record — это не опечатка. А ссылка в никуда из діагностика.md — опечатка.
        WriteSz("160306", FullSet("160306"));
        File.WriteAllText(Path.Combine(Sz("160306"), "160306.md"),
            "замовлення: [[6114675]]\nпристрій: [[ARTLINE Gaming X43]]\n![[висновок]]\n");
        File.WriteAllText(Path.Combine(Sz("160306"), "діагностика.md"),
            "див. [[Симптоми/яких-немає]]\n");

        var issues = KbDoctor.Check(_root);

        Assert.Equal(2, issues.Count(i => i.Severity == "template"));
        Assert.Contains(issues, i => i.Severity == "warn" && i.What.Contains("яких-немає"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
