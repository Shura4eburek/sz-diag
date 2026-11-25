using System.Text;
using Microsoft.Extensions.Configuration;

namespace SzDiag.Updater;

/// <summary>Конфиг клиента, вшитый в exe при сборке (build-dist кладёт dist\client\appsettings.json
/// в ресурс). На клиент достаточно принести ОДИН SzDiag.Updater.exe: настройки он берёт из себя, а
/// агенту при первом запуске кладёт рядом appsettings.json. Файл рядом с exe по-прежнему главнее —
/// правки на месте не затираются.</summary>
public static class EmbeddedConfig
{
    public const string ResourceName = "SzDiag.Updater.embedded-client.json";

    /// <summary>Вшитый конфиг или null (сборка из исходников без build-dist).</summary>
    public static string? Read()
    {
        using var s = typeof(EmbeddedConfig).Assembly.GetManifestResourceStream(ResourceName);
        if (s is null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        var text = r.ReadToEnd();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Слои: вшитый → appsettings.json рядом с exe → переменные SZUPDATER_*.</summary>
    public static IConfiguration Build(string baseDir, string? embedded)
    {
        var b = new ConfigurationBuilder().SetBasePath(baseDir);
        if (embedded is not null) b.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(embedded)));
        return b.AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("SZUPDATER_")
            .Build();
    }

    /// <summary>Положить appsettings.json рядом с exe, если его нет: агент читает конфиг оттуда, а
    /// пакет обновления appsettings.json не несёт (PackageApplier его пропускает). Существующий
    /// файл не трогаем. Возвращает текст для лога или null, если делать было нечего.</summary>
    public static string? WriteIfMissing(string baseDir, string? embedded)
    {
        if (embedded is null) return null;
        var path = Path.Combine(baseDir, "appsettings.json");
        if (File.Exists(path)) return null;
        try
        {
            File.WriteAllText(path, embedded, new UTF8Encoding(false));
            return $"конфиг клиента развёрнут из exe: {path}";
        }
        catch (Exception ex)
        {
            return $"не смог положить {path}: {ex.Message} — апдейтер работает на вшитом конфиге, агенту он не достанется";
        }
    }
}
