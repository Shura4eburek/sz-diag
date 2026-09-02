namespace SzDiag.Erp.Tests;

/// <summary>
/// Заметку по заявке человек дополняет руками по ходу ремонта, поэтому повторный фетч
/// обязан переписывать только свой блок и ничего не съедать вокруг.
/// </summary>
public class MarkedBlockTests
{
    [Fact]
    public void В_пустой_текст_блок_добавляется_с_маркерами()
    {
        var result = MarkedBlock.Upsert("", "тіло");

        Assert.Contains(MarkedBlock.Begin, result);
        Assert.Contains("тіло", result);
        Assert.Contains(MarkedBlock.End, result);
    }

    [Fact]
    public void Ручной_текст_без_маркеров_сохраняется_целиком()
    {
        var manual = "# Дефект — СЗ 160800\n\nклієнт каже: гасне під грою\n";

        var result = MarkedBlock.Upsert(manual, "тіло");

        Assert.StartsWith(manual, result);
        Assert.Contains("тіло", result);
    }

    [Fact]
    public void Повторная_вставка_заменяет_блок_а_не_дублирует()
    {
        var once = MarkedBlock.Upsert("шапка\n", "перше");

        var twice = MarkedBlock.Upsert(once, "друге");

        Assert.Contains("друге", twice);
        Assert.DoesNotContain("перше", twice);
        Assert.Equal(1, CountOf(twice, MarkedBlock.Begin));
        Assert.Equal(1, CountOf(twice, MarkedBlock.End));
    }

    [Fact]
    public void Текст_до_и_после_блока_переживает_замену()
    {
        var text = MarkedBlock.Upsert("до\n", "перше") + "\nпісля\n";

        var result = MarkedBlock.Upsert(text, "друге");

        Assert.StartsWith("до\n", result);
        Assert.EndsWith("після\n", result);
        Assert.Contains("друге", result);
    }

    [Fact]
    public void Незакрытый_маркер_ничего_не_съедает()
    {
        // Человек снёс половину блока руками: закрывающего маркера нет.
        var broken = $"важливий текст\n{MarkedBlock.Begin}\nогризок\n";

        var result = MarkedBlock.Upsert(broken, "нове");

        Assert.Contains("важливий текст", result);
        Assert.Contains("огризок", result);
        Assert.Contains("нове", result);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
