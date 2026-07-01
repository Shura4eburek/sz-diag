using SzDiag.Kb;

namespace SzDiag.Cli;

/// <summary>Разбор подкоманд `kb record` и `kb search`.</summary>
public static class KbCommand
{
    public static Task RunAsync(string[] args, string kbRoot)
    {
        var paths = new KbPaths(kbRoot);
        var sub = args[0].ToLowerInvariant();

        if (sub == "record" && args.Length >= 2)
        {
            var flags = ParseFlags(args[2..]);
            var req = new RecordRequest
            {
                Sz = args[1],
                Order = Single(flags, "order"),
                Device = Single(flags, "device"),
                Defects = Many(flags, "defect"),
                Replaced = Many(flags, "replaced"),
                Findings = Many(flags, "finding"),
                Actions = Many(flags, "action"),
            };
            var scaffolder = new KnowledgeBaseScaffolder(kbRoot);
            new KbRecorder(paths, scaffolder, new EntityNoteWriter(paths)).Record(req);
            Console.WriteLine($"СЗ {req.Sz}: записано в базу знаний.");
            return Task.CompletedTask;
        }

        if (sub == "search")
        {
            var flags = ParseFlags(args[1..]);
            var results = new KbSearcher(paths).Search(Single(flags, "order"), Single(flags, "text"));
            if (results.Count == 0) { Console.WriteLine("Ничего не найдено."); return Task.CompletedTask; }
            foreach (var r in results)
                Console.WriteLine($"  {r.Sz}  заказ={Clean(r.Order)}  дефект={string.Join(",", r.Defects.Select(Clean))}  заменено={string.Join(",", r.Replaced.Select(Clean))}");
            return Task.CompletedTask;
        }

        Console.WriteLine("""
            Использование:
              szcli kb record <СЗ> [--order X] [--device X] [--defect X]... [--replaced X]... [--finding "..."]... [--action "..."]...
              szcli kb search [--order X] [--text "..."]
            """);
        return Task.CompletedTask;
    }

    private static string Clean(string raw) => raw.Trim('"').Replace("[[", "").Replace("]]", "");

    private static Dictionary<string, List<string>> ParseFlags(string[] args)
    {
        var map = new Dictionary<string, List<string>>();
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..].ToLowerInvariant();
            if (!map.TryGetValue(key, out var list)) { list = new List<string>(); map[key] = list; }
            list.Add(args[i + 1]);
        }
        return map;
    }

    private static string? Single(Dictionary<string, List<string>> flags, string key)
        => flags.TryGetValue(key, out var v) ? v[^1] : null;

    private static IReadOnlyList<string> Many(Dictionary<string, List<string>> flags, string key)
        => flags.TryGetValue(key, out var v) ? v : Array.Empty<string>();
}
