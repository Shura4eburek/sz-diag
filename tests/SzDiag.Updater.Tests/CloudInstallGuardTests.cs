using SzDiag.Updater;
using Xunit;

namespace SzDiag.Updater.Tests;

/// <summary>Точка входа на клиенте (`SzDiag.Updater.exe`) снова оказалась в OneDrive
/// (СЗ 160636, 04.08, бэклог п.41): агента положили в `C:\Users\User\OneDrive\...\Client-test`,
/// и `state.json`/логи текущей сессии стали синхронизироваться в личное облако клиента —
/// то, что должно быть временным и откатываться без следов. Апдейтер обязан отказаться
/// стартовать из такой папки, а не молча продолжить (той же грабле, что и с тулами, п.63).</summary>
public class CloudInstallGuardTests
{
    [Theory]
    [InlineData(@"C:\Users\User\OneDrive\Робочий стіл\Client-test")]
    [InlineData(@"C:\Users\msi-pc\OneDrive\Desktop\Client-test")]
    [InlineData(@"C:\Users\u\Dropbox\szdiag")]
    public void Check_CloudPath_ReturnsWarning(string baseDir)
    {
        var warning = CloudInstallGuard.Check(baseDir);

        Assert.NotNull(warning);
        Assert.Contains(baseDir, warning);
    }

    [Theory]
    [InlineData(@"C:\Client-test")]
    [InlineData(@"C:\szdiag\client")]
    public void Check_LocalPath_ReturnsNull(string baseDir)
        => Assert.Null(CloudInstallGuard.Check(baseDir));
}
