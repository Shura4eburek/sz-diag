using System.Linq;
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

        // Незащищённая от WU-перезагрузки машина обязана быть видна сразу в шапке — на
        // 160705/161716 заморозку забывали на дни, и это всплывало только на закрытии СЗ
        // (бэклог п.139/150).
        if (report.WuFrozen == false)
        {
            sb.AppendLine("⚠ **Windows Update НЕ заморожен** — обновления могут прилететь "
                          + "прямо во время диагностики. Останови и сравни: `szcli freeze <СЗ>`.");
            sb.AppendLine();
        }

        // Упавшая секция обязана быть видна сразу в шапке, а не только внутри своего
        // "```"-блока: на 161716 whea упала целиком (длинный путь клиента), и отчёт без
        // этой строки выглядел полным (бэклог п.149).
        var failed = report.Steps.Where(s => s.Error is not null).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("## Провалившиеся секции");
            sb.AppendLine();
            foreach (var f in failed)
                sb.AppendLine($"- секция {f.Name}: НЕ ОТРАБОТАЛА ({f.Error})");
            sb.AppendLine();
        }

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

        // Сводка последней строкой отчёта: на 161972 whea упала с ошибкой запуска процесса
        // (длинное имя командной строки), а заметно это было только тому, кто дочитал diag.md
        // до конца - шапка «провалившиеся секции» не спасает, если её саму пролистали
        // (бэклог п.182). Печатается всегда, даже когда всё отработало.
        var summary = $"Секций запрошено: {report.Steps.Count}, выполнено: {report.Steps.Count - failed.Count}";
        if (failed.Count > 0)
            summary += ", провалено: " + string.Join(", ", failed.Select(f => $"{f.Name} ({f.Error})"));
        sb.AppendLine(summary + ".");
        return sb.ToString();
    }
}
