using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace SzDiag.Desk.Services;

/// <summary>Мигание окна в панели задач, пока оно не в фокусе. Вместо всплывающего уведомления
/// Windows: у Avalonia 11 нативных тостов нет, а мигание работает без зависимостей.</summary>
public static class WindowAttention
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private const uint FlashAll = 3;          // FLASHW_ALL: заголовок и кнопка в панели задач
    private const uint FlashUntilFocus = 12;  // FLASHW_TIMERNOFG: до получения фокуса

    public static void Flash(Window w)
    {
        if (!OperatingSystem.IsWindows() || w.IsActive) return;
        var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = FlashAll | FlashUntilFocus,
        };
        FlashWindowEx(ref info);
    }
}
