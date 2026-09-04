using System.Reflection;

namespace SzDiag.Cli;

/// <summary>Список команд CLI и версия сборки (бэклог п.198): протухший `szcli` из dist на
/// неизвестную команду печатал общий usage и выходил с кодом 0 — «note не работает» дважды
/// списывалось на кавычки/кириллицу, пока не выяснилось, что в бинаре команды просто нет.</summary>
public static class CliCommands
{
    /// <summary>Все команды верхнего уровня. Новая команда в switch — добавь и сюда,
    /// иначе она будет объявляться «неизвестной».</summary>
    public static readonly string[] Known =
    {
        "watch", "list", "close", "target", "exec", "pull", "push", "reboots",
        "freeze", "unfreeze", "note", "sensors", "test", "diag", "kb", "hw",
        "client", "maintenance", "agent", "sz", "stress", "app",
    };

    public static bool IsKnown(string command)
        => Known.Contains(command, StringComparer.OrdinalIgnoreCase);

    /// <summary>«1.0.0+abc1234, сборка 2026-08-22» — чтобы протухший бинарь был виден сразу.</summary>
    public static string Describe()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        string built;
        try
        {
            var exe = Environment.ProcessPath;
            built = exe is null ? "?" : File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
        }
        catch { built = "?"; }
        return $"szcli {version}, сборка {built}";
    }
}
