namespace SzDiag.Desk.Services;

/// <summary>Каталог новых сессий заявок с лёгким CLAUDE.md вместо 54-килобайтного CLAUDE.md
/// разработчика: тот целиком ехал в контекст каждого хода, а кэш сообщений `claude -p` на Opus
/// между ходами теряется — каждое сообщение стоило ~$0.26 (бэклог п.266). Файл пишется заново
/// на каждый старт Desk: пути (kb, szcli) и шаблон могли измениться.</summary>
public static class SessionWorkspace
{
    public const string TemplateResource = "sz-session-CLAUDE.md";

    public static string Render(string template, string repo, string kb, string? szcli)
        => template.Replace("{{REPO}}", repo).Replace("{{KB}}", kb).Replace("{{SZCLI}}", szcli ?? "szcli");

    public static void Write(string dir, string repo, string kb, string? szcli)
    {
        using var s = typeof(SessionWorkspace).Assembly.GetManifestResourceStream(TemplateResource)
                      ?? throw new InvalidOperationException($"нет встроенного шаблона {TemplateResource}");
        using var r = new StreamReader(s);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), Render(r.ReadToEnd(), repo, kb, szcli));
    }
}
