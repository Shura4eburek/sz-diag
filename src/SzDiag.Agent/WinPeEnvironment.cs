namespace SzDiag.Agent;

/// <summary>Определение среды WinPE. Нужно там, где обычная винда и PE ведут себя
/// по-разному, а флаг <c>--pe</c> до кода не доходит (например, PowerShellRunner создаётся
/// до разбора аргументов).
///
/// Признак — системный диск <c>X:</c>: в PE образ разворачивается на RAM-диск X:, на живой
/// машине там всегда буква реального тома. Дополнительно проверяем каталог
/// <c>%SystemRoot%\System32\startnet.cmd</c> — он существует только в PE.</summary>
public static class WinPeEnvironment
{
    private static readonly Lazy<bool> Detected = new(Detect);

    /// <summary>true, если процесс исполняется в WinPE.</summary>
    public static bool IsWinPe => Detected.Value;

    private static bool Detect()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(root)) return false;
            var onRamDisk = root.StartsWith("X:", StringComparison.OrdinalIgnoreCase);
            var hasStartnet = File.Exists(Path.Combine(root, "System32", "startnet.cmd"));
            return onRamDisk && hasStartnet;
        }
        catch
        {
            return false;   // не смогли определить — считаем обычной виндой
        }
    }
}
