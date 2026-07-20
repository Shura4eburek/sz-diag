using System.Text;

namespace SzDiag.Kb;

/// <summary>Собирает diag.md из TestReport: одна секция на шаг, только вывод (без сырой
/// PowerShell-команды — имя секции самодостаточно). Все шаги диагностики — Command.</summary>
public static class DiagReportBuilder
{
    public static string Build(TestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Диагностика — СЗ {report.Sz}");
        sb.AppendLine();
        sb.AppendLine($"- Хост: {report.Hostname}");
        sb.AppendLine($"- Дата: {report.RunAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var s in report.Steps)
        {
            sb.AppendLine($"## {s.Name}");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(s.Error is not null ? $"ошибка: {s.Error}" : (s.Output ?? "").TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
