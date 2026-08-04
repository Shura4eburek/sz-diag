using System.Runtime.InteropServices;

namespace SzDiag.ConsoleUi;

/// <summary>Реальная консоль Windows. Пишет через переданный writer — тот же
/// (залоченный) поток, что и весь остальной вывод процесса.</summary>
public sealed class SystemTerminalSurface : ITerminalSurface
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly TextWriter _out;

    public SystemTerminalSurface(TextWriter output) => _out = output;

    /// <summary>Включает обработку escape-последовательностей. false — старый conhost
    /// без VT: escape-коды в нём напечатались бы как мусор, липкий режим не включаем.</summary>
    public static bool TryEnableVirtualTerminal()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
    }

    public int Width { get { try { return Console.WindowWidth; } catch { return 0; } } }
    public int Height { get { try { return Console.WindowHeight; } catch { return 0; } } }
    public bool OutputRedirected { get { try { return Console.IsOutputRedirected; } catch { return true; } } }

    public void Write(string raw) => _out.Write(raw);
}
