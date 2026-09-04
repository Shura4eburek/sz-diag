namespace SzDiag.Agent.Tests;

/// <summary>M-5 (ревью волны 1): поиск корня репо вверх по дереву от <see cref="AppContext.BaseDirectory"/>
/// до <c>SzDiag.sln</c> — один и тот же приём, написанный двумя агентами независимо
/// (<c>BuildDistScriptTests.RepoRoot</c> и <c>BugcheckCodesRecipeSyncTests.RecipePath</c>).
/// Единственное общее место, а не дубли.</summary>
internal static class TestPaths
{
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }
}
