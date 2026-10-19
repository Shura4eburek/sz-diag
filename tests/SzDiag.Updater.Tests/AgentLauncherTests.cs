using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

/// <summary>Апдейтер — точка входа на клиенте, поэтому всё, что ему передали, должно
/// доезжать до агента: на 164266 понадобился автозапуск `SzDiag.Updater.exe 164266`
/// из RunOnce, а агент номер СЗ не получал и молча ждал ввода с консоли.</summary>
public class AgentLauncherTests
{
    [Fact]
    public void Sanitize_пропускает_номер_СЗ_и_флаги()
    {
        var result = AgentLauncher.Sanitize(new[] { "164266", "--resume" });
        Assert.Equal(new[] { "164266", "--resume" }, result);
    }

    [Fact]
    public void Sanitize_выбрасывает_пустые_и_пробельные()
    {
        var result = AgentLauncher.Sanitize(new[] { "", "  ", "164266" });
        Assert.Equal(new[] { "164266" }, result);
    }

    [Fact]
    public void Sanitize_обрезает_пробелы_по_краям()
    {
        // Аргумент из RunOnce/ярлыка легко приезжает с хвостовым пробелом, а SzNumber
        // требует ровно шесть цифр — агент отказался бы с «некорректный номер СЗ».
        var result = AgentLauncher.Sanitize(new[] { " 164266 " });
        Assert.Equal(new[] { "164266" }, result);
    }

    [Fact]
    public void Sanitize_на_пустом_списке_даёт_пустой()
    {
        Assert.Empty(AgentLauncher.Sanitize(Array.Empty<string>()));
    }
}
