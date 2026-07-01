using System.Text.Json;

namespace SzDiag.Agent;

/// <summary>Сохранение/загрузка RevertState в файл (переживает краш; читается watchdog'ом).</summary>
public static class RevertStateStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(string path, RevertState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, Opts));
    }

    public static RevertState? Load(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<RevertState>(File.ReadAllText(path)) : null;

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
