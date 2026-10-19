using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

/// <summary>Тема Shirabe: токены на месте, экраны берут вид только из них, шрифты едут в exe.</summary>
public class ThemeTests
{
    private static readonly string[] Tokens =
    [
        "Bg.Base", "Bg.Surface1", "Bg.Surface2", "Bg.Surface3", "Bg.Code", "Bg.Hover", "Bg.Pressed",
        "Bg.Selected", "Bg.SelectedInactive", "Bg.Disabled", "Bg.Danger",
        "Border.Subtle", "Border.Default", "Border.Control", "Border.ControlHover", "Border.Focus",
        "Text.Primary", "Text.Secondary", "Text.Disabled", "Text.OnAccent",
        "Accent.Default", "Accent.Hover", "Accent.Pressed", "Accent.Text", "Accent.Subtle",
        "Status.Running", "Status.Waiting", "Status.Approval", "Status.Error", "Status.Done",
        "Status.Success", "Status.Warning", "Diff.AddBg", "Diff.DelBg",
        "Space.0-5", "Space.1", "Space.1-5", "Space.2", "Space.3", "Space.4", "Space.5", "Space.6",
        "Radius.Xs", "Radius.Sm", "Radius.Md", "Radius.Lg", "Radius.Island", "Radius.Full",
        "Size.Toolbar", "Size.Tab", "Size.Control", "Size.ControlSm", "Size.Row", "Size.Statusbar", "Size.Icon",
        "Font.Ui", "Font.Mono",
    ];

    private static string ViewsDir()
        => Path.Combine(DeskClaudeHost.FindRepoRoot(AppContext.BaseDirectory)!, "src", "SzDiag.Desk", "Views");

    private static IEnumerable<string> ViewFiles()
        => Directory.EnumerateFiles(ViewsDir(), "*.axaml", SearchOption.AllDirectories);

    [AvaloniaFact]
    public void AllTokens_Resolve()
    {
        foreach (var key in Tokens)
            Assert.True(Application.Current!.TryGetResource(key, null, out _), $"нет токена {key}");
    }

    [AvaloniaFact]
    public void Tokens_MatchShirabeValues()
    {
        Assert.Equal(Color.Parse("#131417"), Brush("Bg.Base"));
        Assert.Equal(Color.Parse("#1e1f22"), Brush("Bg.Surface1"));
        Assert.Equal(Color.Parse("#2f6ae0"), Brush("Accent.Default"));
        Assert.Equal(Color.Parse("#62bb68"), Brush("Status.Running"));
        Assert.Equal(Color.Parse("#e3a53c"), Brush("Status.Approval"));
        Assert.Equal(Color.Parse("#f06a62"), Brush("Status.Error"));
    }

    private static Color Brush(string key)
        => Application.Current!.TryGetResource(key, null, out var v) && v is ISolidColorBrush b ? b.Color : default;

    [Fact]
    public void Views_HaveNoColorLiterals()
    {
        var hex = new Regex("#[0-9a-fA-F]{6,8}\\b");
        var bad = ViewFiles().Where(f => hex.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        Assert.True(bad.Count == 0, "литералы цвета в: " + string.Join(", ", bad));
    }

    [Fact]
    public void Views_HaveNoEmoji()
    {
        var emoji = new[] { "⚡", "🧊", "💬", "■", "⚠" };
        var bad = ViewFiles().Where(f => emoji.Any(File.ReadAllText(f).Contains)).Select(Path.GetFileName).ToList();
        Assert.True(bad.Count == 0, "эмодзи в: " + string.Join(", ", bad));
    }

    /// <summary>Каждый StaticResource экранов существует: опечатка в ключе иначе всплывёт только в окне.</summary>
    [AvaloniaFact]
    public void Views_StaticResources_Exist()
    {
        var re = new Regex("StaticResource ([A-Za-z0-9.\\-]+)\\}");
        foreach (var f in ViewFiles())
            foreach (Match m in re.Matches(File.ReadAllText(f)))
                Assert.True(Application.Current!.TryGetResource(m.Groups[1].Value, null, out _),
                    $"{Path.GetFileName(f)}: нет ресурса {m.Groups[1].Value}");
    }

    /// <summary>Inter во всех весах шкалы (400/500/600) находится — иначе окно падает на первом
    /// же body-medium (так было под заглушкой шрифтов headless).</summary>
    [AvaloniaTheory]
    [InlineData(FontWeight.Normal)]
    [InlineData(FontWeight.Medium)]
    [InlineData(FontWeight.SemiBold)]
    public void InterFont_ResolvesAllWeights(FontWeight weight)
    {
        Assert.True(Application.Current!.TryGetResource("Font.Ui", null, out var f) && f is FontFamily);
        var tf = new Typeface((FontFamily)f!, FontStyle.Normal, weight);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(tf, out var g), $"Inter {weight} не найден");
        Assert.StartsWith("Inter", g!.FamilyName);   // «Inter Medium» — сам файл веса, а не подмена системным
    }

    [Theory]
    [InlineData("JetBrainsMono-Regular.ttf", "JetBrains Mono")]
    public void Fonts_EmbeddedWithFamilyFromTokens(string file, string family)
    {
        // Гарнитура по `avares://…/Fonts#JetBrains Mono` ищется по имени семейства из таблицы name.
        var path = Path.Combine(DeskClaudeHost.FindRepoRoot(AppContext.BaseDirectory)!,
            "src", "SzDiag.Desk", "Assets", "Fonts", file);
        Assert.Contains(family, FamilyNames(File.ReadAllBytes(path)));
    }

    private static HashSet<string> FamilyNames(byte[] ttf)
    {
        static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        static int U32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        var names = new HashSet<string>();
        var tables = U16(ttf, 4);
        for (var i = 0; i < tables; i++)
        {
            var rec = 12 + i * 16;
            if (Encoding.ASCII.GetString(ttf, rec, 4) != "name") continue;
            var name = U32(ttf, rec + 8);
            var count = U16(ttf, name + 2);
            var strings = name + U16(ttf, name + 4);
            for (var j = 0; j < count; j++)
            {
                var r = name + 6 + j * 12;
                var platform = U16(ttf, r);
                var nameId = U16(ttf, r + 6);
                if (nameId is not (1 or 16) || platform != 3) continue;
                names.Add(Encoding.BigEndianUnicode.GetString(ttf, strings + U16(ttf, r + 10), U16(ttf, r + 8)));
            }
        }
        return names;
    }
}
