namespace SzDiag.Cli;

/// <summary>Разбор хвоста `test run &lt;СЗ&gt; …`: фильтр набора плюс метка конфигурации.
/// Вынесено из Program.cs отдельно, чтобы разбор был покрыт тестами (top-level statements
/// напрямую не тестируются).</summary>
public sealed record TestRunArgs(string? Filter, string? Config, bool SameConfig)
{
    public static TestRunArgs Parse(string[] rest)
    {
        string? config = null;
        var sameConfig = false;
        var positional = new List<string>();

        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                // Флаг без значения не глотаем молча: пустая метка — та же потеря контекста,
                // hub всё равно откажет и подскажет, чего не хватает.
                if (i + 1 < rest.Length) config = rest[++i];
                continue;
            }
            if (rest[i].Equals("--same-config", StringComparison.OrdinalIgnoreCase))
            {
                sameConfig = true;
                continue;
            }
            positional.Add(rest[i]);
        }

        return new TestRunArgs(positional.Count > 0 ? positional[0] : null, config, sameConfig);
    }
}
