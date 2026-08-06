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
            // Ошибка и вывод печатаются ВМЕСТЕ: раньше `код 1` затирал всё, что секция успела
            // собрать, и отчёт по «Истории сбоев» состоял из одной строки `ошибка: код 1:`
            // (бэклог п.74). Ошибка идёт первой строкой, дальше — то, что всё же собралось.
            if (s.Error is not null) sb.AppendLine($"ошибка: {s.Error}");
            var body = (s.Output ?? "").TrimEnd();
            if (body.Length > 0) sb.AppendLine(body);
            else if (s.Error is null) sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
