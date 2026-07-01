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
            else // Screenshot
            {
                sb.AppendLine(s.Error is not null
                    ? $"скрин недоступен: {s.Error}"
                    : $"![[{s.ScreenshotFile}]]");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
