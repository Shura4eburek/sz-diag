namespace SzDiag.Desk.Services;

/// <summary>Заморожен ли Windows Update на СЗ — тот же признак, что у `szcli list`: файл прежних
/// значений `cli\freeze\&lt;СЗ&gt;.json` рядом с szcli (его заводит freeze и снимает unfreeze).</summary>
public sealed class FreezeProbe(Func<string?> szcliCmd)
{
    public bool IsFrozen(string sz)
        => szcliCmd() is { } cmd
           && File.Exists(Path.Combine(Path.GetDirectoryName(cmd)!, "cli", "freeze", $"{sz}.json"));
}
