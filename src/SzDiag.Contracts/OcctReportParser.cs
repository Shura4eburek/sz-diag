using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SzDiag.Contracts;

/// <summary>Один выполненный период из отчёта OCCT (<c>occt-report.html</c>). Секунды, а не
/// <c>TimeSpan</c>, напрямую в поле записи — DTO уходит по HTTP (`szcli test result`), а
/// System.Text.Json без кастомного конвертера TimeSpan не сериализует (тот же приём, что у
/// <c>RebootEvent</c> с `*Seconds`-полями).</summary>
/// <param name="ExecutedDurationSeconds">null — поле в отчёте отсутствует/не парсится.</param>
public sealed record OcctPeriodResult(string TestType, double? ExecutedDurationSeconds, int Errors, int WheaErrors)
{
    public TimeSpan? ExecutedDuration => ExecutedDurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
}

/// <summary>Итог разбора <c>occt-report.html</c>: периоды + суммарное фактическое время.</summary>
public sealed record OcctReportSummary(IReadOnlyList<OcctPeriodResult> Periods, double? ElapsedSeconds)
{
    public TimeSpan? Elapsed => ElapsedSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
    public int TotalErrors => Periods.Sum(p => p.Errors);
    public int TotalWheaErrors => Periods.Sum(p => p.WheaErrors);
}

/// <summary>Разбор HTML-отчёта OCCT: тул зашивает результат прогона (периоды, ошибки, WHEA,
/// фактическую длительность) в JS-переменную <c>scheduleExecutionCompressed</c> — base64 от
/// gzip'нутого JSON. Ручная распаковка (бэклог п.124, СЗ 161346) отняла время именно тогда,
/// когда решался вопрос «10 минут вместо 180 — это провал прогона или нет»:
///
/// <code>
/// periodExecutions[0] Combined     executedDuration 00:05:00  errors 0  wheaErrors 0
/// periodExecutions[1] PowerSupply  executedDuration 00:05:00  errors 0  wheaErrors 0
/// elapsed 00:10:20
/// </code>
///
/// Разбор специально терпимый к точной форме JSON внутри (регистр имён полей, вложенность
/// массива периодов) — формат не документирован публично, а версии OCCT могли его слегка
/// поменять; лучше разобрать частично, чем не разобрать вовсе.</summary>
public static class OcctReportParser
{
    private static readonly Regex CompressedField = new(
        "scheduleExecutionCompressed[\"']?\\s*[:=]\\s*[\"']([A-Za-z0-9+/=]+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>null — в HTML нет распознаваемого поля, либо base64/gzip/JSON не разобрались.</summary>
    public static OcctReportSummary? TryParse(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var match = CompressedField.Match(html);
        if (!match.Success) return null;

        string json;
        try
        {
            var bytes = Convert.FromBase64String(match.Groups[1].Value);
            json = Decompress(bytes);
        }
        catch { return null; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var periodsElement = FindArrayByName(doc.RootElement, "periodexecutions", depth: 0);
            var periods = new List<OcctPeriodResult>();
            if (periodsElement is { ValueKind: JsonValueKind.Array } pe)
            {
                foreach (var p in pe.EnumerateArray())
                {
                    var testType = GetStringCI(p, "testType") ?? "?";
                    var duration = ParseTimeSpanCI(p, "executedDuration");
                    var errors = GetIntCI(p, "errors") ?? 0;
                    var wheaErrors = GetIntCI(p, "wheaErrors") ?? 0;
                    periods.Add(new OcctPeriodResult(testType, duration?.TotalSeconds, errors, wheaErrors));
                }
            }
            var elapsed = ParseTimeSpanCI(doc.RootElement, "elapsed");
            return new OcctReportSummary(periods, elapsed?.TotalSeconds);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Gzip — стандартное сжатие для этого поля; на случай будущей смены формата
    /// откатываемся к «как есть» (вдруг это просто plain JSON без сжатия).</summary>
    private static string Decompress(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>Ищет массив с именем свойства (без учёта регистра) на любом уровне вложенности —
    /// неизвестно заранее, лежит ли <c>periodExecutions</c> в корне или под <c>schedule</c>/
    /// <c>result</c>.</summary>
    private static JsonElement? FindArrayByName(JsonElement element, string nameLower, int depth)
    {
        if (depth > 6) return null; // предохранитель от патологически глубокого JSON
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array
                    && prop.Name.Replace("_", "").Equals(nameLower, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }
            foreach (var prop in element.EnumerateObject())
            {
                var found = FindArrayByName(prop.Value, nameLower, depth + 1);
                if (found is not null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindArrayByName(item, nameLower, depth + 1);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static string? GetStringCI(JsonElement obj, string name)
        => TryGetPropertyCI(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetIntCI(JsonElement obj, string name)
    {
        if (!TryGetPropertyCI(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static TimeSpan? ParseTimeSpanCI(JsonElement obj, string name)
    {
        if (!TryGetPropertyCI(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String when TimeSpan.TryParse(v.GetString(), out var ts) => ts,
            // Некоторые версии могли бы отдавать секунды числом — на всякий случай.
            JsonValueKind.Number when v.TryGetDouble(out var secs) => TimeSpan.FromSeconds(secs),
            _ => null,
        };
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.NameEquals(name)) { value = prop.Value; return true; }
            }
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = prop.Value; return true; }
            }
        }
        value = default;
        return false;
    }
}
