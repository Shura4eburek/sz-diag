using SzDiag.Contracts;

namespace SzDiag.Agent.Tests;

/// <summary>Таблица кодов LiveKernelEvent должна жить в одном месте — <see cref="BugcheckCodes"/>.
/// Офлайн-рецепт <c>pe-wer-livekernel.ps1</c> не может вызвать C# (agent.exe — self-contained
/// single-file, DLL рядом нет), поэтому держит текстовую копию между маркерами; на 161211 два
/// самых массовых кода (62% событий) были расшифрованы в рецепте, но не в RunDiag — этот тест
/// ловит расхождение сразу, а не на следующей живой заявке (бэклог п.197).</summary>
public class BugcheckCodesRecipeSyncTests
{
    private const string BeginMarker = "# BEGIN bugcheck-codes (generated - see BugcheckCodes.ToPowerShellRecipeTable, do not edit by hand)";
    private const string EndMarker = "# END bugcheck-codes";

    private static string RecipePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
        return Path.Combine(dir, "tools", "recipes", "client", "pe-wer-livekernel.ps1");
    }

    [Fact]
    public void RecipeTable_MatchesGeneratedFromBugcheckCodes()
    {
        var text = File.ReadAllText(RecipePath());
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start,
            "маркеры BEGIN/END bugcheck-codes не найдены в pe-wer-livekernel.ps1");

        var block = text[(start + BeginMarker.Length)..end].Trim('\r', '\n');
        var expected = BugcheckCodes.ToPowerShellRecipeTable().Trim('\r', '\n');

        Assert.Equal(expected.ReplaceLineEndings("\n"), block.ReplaceLineEndings("\n"));
    }
}
