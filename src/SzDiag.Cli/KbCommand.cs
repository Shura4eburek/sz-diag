using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Cli;

/// <summary>Разбор подкоманд `kb record`, `kb summary`, `kb search`, `kb rm`.</summary>
public static class KbCommand
{
    public static Task<int> RunAsync(string[] args, string kbRoot)
    {
        var paths = new KbPaths(kbRoot);
        var sub = args[0].ToLowerInvariant();

        // Справку разбираем ДО номера СЗ: иначе `szcli kb record --help` заводил в базе
        // знаний заявку с номером «--help» (бэклог п.57).
        if (sub is "--help" or "-h" or "help" || args.Any(a => a is "--help" or "-h"))
        {
            PrintUsage();
            return Task.FromResult(0);
        }

        if (sub == "record" && args.Length >= 2)
        {
            if (!Validate(args[1], out var recordCode)) return Task.FromResult(recordCode);
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
                Symptoms = Many(flags, "symptom"),
                Status = Single(flags, "status"),
                Verdict = Single(flags, "verdict"),
            };
            var scaffolder = new KnowledgeBaseScaffolder(kbRoot);
            new KbRecorder(paths, scaffolder, new EntityNoteWriter(paths)).Record(req);
            Console.WriteLine($"СЗ {req.Sz}: записано в базу знаний.");
            return Task.FromResult(0);
        }

        if (sub == "summary" && args.Length >= 2)
        {
            if (!Validate(args[1], out var summaryCode)) return Task.FromResult(summaryCode);
            var path = new KnowledgeBaseScaffolder(kbRoot).EnsureSummarySkeleton(args[1]);
            Console.WriteLine($"СЗ {args[1]}: скелет вывода — {path}");
            return Task.FromResult(0);
        }

        // rm: убрать мусорную/ошибочную СЗ целиком — вместе с пустыми каталогами, которые
        // git не удаляет, а Obsidian продолжает показывать в дереве.
        if (sub == "rm" && args.Length >= 2)
        {
            if (!Validate(args[1], out var rmCode)) return Task.FromResult(rmCode);
            var result = new KbRemover(paths).Remove(args[1]);
            if (!result.Existed)
            {
                Console.WriteLine($"СЗ {args[1]}: в базе знаний нет ({result.Path}).");
                return Task.FromResult(1);
            }
            Console.WriteLine($"СЗ {args[1]}: удалено {result.FilesRemoved} файлов — {result.Path}");
            foreach (var link in result.IncomingLinks)
                Console.WriteLine($"  ⚠ на СЗ ссылались: {link}");
            return Task.FromResult(0);
        }

        if (sub == "search")
        {
            var flags = ParseFlags(args[1..]);
            var results = new KbSearcher(paths).Search(Single(flags, "order"), Single(flags, "text"));
            if (results.Count == 0) { Console.WriteLine("Ничего не найдено."); return Task.FromResult(0); }
            foreach (var r in results)
                Console.WriteLine($"  {r.Sz}  заказ={Clean(r.Order)}  дефект={string.Join(",", r.Defects.Select(Clean))}  заменено={string.Join(",", r.Replaced.Select(Clean))}");
            return Task.FromResult(0);
        }

        PrintUsage();
        return Task.FromResult(2);
    }

    /// <summary>Проверяет номер СЗ до любых операций с диском: скелет заметок под мусорный
    /// ввод создавать нельзя (бэклог п.57).</summary>
    private static bool Validate(string sz, out int exitCode)
    {
        if (SzNumber.IsValid(sz)) { exitCode = 0; return true; }
        Console.Error.WriteLine($"Неверный номер СЗ: {SzNumber.Explain(sz)}.");
        exitCode = 2;
        return false;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Использование:
          szcli kb record <СЗ> [--order X] [--device X] [--defect X]... [--replaced X]... [--symptom "..."]... [--finding "..."]... [--action "..."]... [--status X] [--verdict X]
          szcli kb summary <СЗ>
          szcli kb search [--order X] [--text "..."]
          szcli kb rm <СЗ>            удалить СЗ из базы знаний целиком
        """);

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
