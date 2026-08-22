using System.Text;

namespace SzDiag.Kb;

/// <summary>Собирает report.md из модели TestReport.</summary>
public static class ReportMarkdownBuilder
{
    public static string Build(TestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Отчёт диагностики — СЗ {report.Sz}");
        sb.AppendLine();
        sb.AppendLine($"- Хост: {report.Hostname}");
        sb.AppendLine($"- Дата: {report.RunAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var s in report.Steps)
        {
            sb.AppendLine($"## {s.Name}");
            sb.AppendLine();
            if (s.Kind == TestStepKind.Command)
            {
                if (s.Command is not null)
                {
                    sb.AppendLine($"`{s.Command}`");
                    sb.AppendLine();
                }
                if (s.Error is not null)
                    sb.AppendLine($"ошибка: {s.Error}");
                else
                {
                    sb.AppendLine("```");
                    sb.AppendLine(s.Output ?? "");
                    sb.AppendLine("```");
                }
            }
            else if (s.Kind == TestStepKind.App)
            {
                if (s.Command is not null)
                {
                    sb.AppendLine($"`{s.Command}`");
                    sb.AppendLine();
                }
                if (s.Error is not null)
                {
                    sb.AppendLine($"ошибка запуска: {s.Error}");
                    sb.AppendLine();
                }
                if (!string.IsNullOrEmpty(s.Output))
                {
                    sb.AppendLine("```");
                    sb.AppendLine(s.Output);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
                // Текстовая ссылка вместо ![[…]]: артефакты живут вне vault (hub кладёт их в
                // pulled\, п.131), а висячий эмбед Obsidian молча резолвит в чужой файл (п.11).
                sb.AppendLine(s.ScreenshotFile is not null
                    ? $"скрин: {s.ScreenshotFile} (вне vault: hub\\pulled\\{report.Sz}\\reports\\)"
                    : "скрин под нагрузкой недоступен");
                if (s.ArtifactFile is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Отчёт: {s.ArtifactFile} (вне vault: hub\\pulled\\{report.Sz}\\reports\\)");
                }
            }
            else // Screenshot
            {
                sb.AppendLine(s.Error is not null
                    ? $"скрин недоступен: {s.Error}"
                    : $"скрин: {s.ScreenshotFile} (вне vault: hub\\pulled\\{report.Sz}\\reports\\)");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
