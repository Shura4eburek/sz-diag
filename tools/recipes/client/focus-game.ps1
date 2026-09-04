$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Вернуть окно игры на передний план после GUI-возни на машине клиента (СЗ 123456).
#
# Грабля: навигация кликами вслепую по чужому рабочему столу промахивается — координаты снимка
# (виртуальные, 2048x1152 при масштабе 125%) и координаты SetCursorPos (физические, 2560x1440)
# не совпадают, а окна перекрывают друг друга. Промах вытащил на передний план Discord поверх
# игры. После любой такой сессии игру возвращаем в фокус явно.
#
#   szcli exec 123456 -f tools\recipes\client\focus-game.ps1 --in-session

$ErrorActionPreference = 'SilentlyContinue'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinFocus {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
}
'@

foreach ($name in 'NVIDIA App') {
    foreach ($p in (Get-Process $name | Where-Object { $_.MainWindowHandle -ne 0 })) {
        [void][WinFocus]::ShowWindow($p.MainWindowHandle, 6)   # SW_MINIMIZE
        '   свёрнуто: {0}' -f $p.ProcessName
    }
}

$g = Get-Process Stalker2-Win64-Shipping | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($g) {
    [void][WinFocus]::ShowWindow($g.MainWindowHandle, 9)       # SW_RESTORE
    [void][WinFocus]::BringWindowToTop($g.MainWindowHandle)
    [void][WinFocus]::SetForegroundWindow($g.MainWindowHandle)
    '   игра поднята на передний план: pid {0} «{1}»' -f $g.Id, $g.MainWindowTitle
} else {
    '   окно игры не найдено'
}
