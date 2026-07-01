using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class FrontmatterEditorTests
{
    private const string Sample =
        "---\nсз: 156864\nзаказ: \"\"\nдефект: []\nзаменено: []\nустройство: \"\"\nдата: 2026-07-01\n---\n\n# СЗ 156864\n\n![[request]]\n";

    [Fact]
    public void SetScalar_OverwritesValue_AndSerializes()
    {
        var fm = FrontmatterEditor.Load(Sample);
        fm.SetScalar("заказ", "\"[[A-2025-0098]]\"");

        var outText = fm.Serialize();

        Assert.Contains("заказ: \"[[A-2025-0098]]\"", outText);
        Assert.Contains("# СЗ 156864", outText);   // тело сохранено
    }

    [Fact]
    public void AddToList_AppendsWithoutDuplicates()
    {
        var fm = FrontmatterEditor.Load(Sample);
        fm.AddToList("дефект", "\"[[Не стартует POST]]\"");
        fm.AddToList("дефект", "\"[[Не стартует POST]]\""); // дубль

        Assert.Single(fm.GetList("дефект"));
        Assert.Contains("дефект: [\"[[Не стартует POST]]\"]", fm.Serialize());
    }

    [Fact]
    public void AddToList_ParsesExistingItems()
    {
        var withOne = Sample.Replace("дефект: []", "дефект: [\"[[A]]\"]");
        var fm = FrontmatterEditor.Load(withOne);
        fm.AddToList("дефект", "\"[[B]]\"");

        Assert.Equal(2, fm.GetList("дефект").Count);
        Assert.Contains("дефект: [\"[[A]]\", \"[[B]]\"]", fm.Serialize());
    }

    [Fact]
    public void GetScalar_ReturnsRawValue()
    {
        var fm = FrontmatterEditor.Load(Sample.Replace("заказ: \"\"", "заказ: \"[[A-1]]\""));
        Assert.Equal("\"[[A-1]]\"", fm.GetScalar("заказ"));
    }

    [Fact]
    public void UnknownKeysAndBody_Preserved()
    {
        var withExtra = Sample.Replace("дата: 2026-07-01", "дата: 2026-07-01\nтег: важное");
        var fm = FrontmatterEditor.Load(withExtra);
        fm.SetScalar("устройство", "\"[[Lenovo]]\"");

        var outText = fm.Serialize();
        Assert.Contains("тег: важное", outText);
        Assert.Contains("![[request]]", outText);
    }
}
