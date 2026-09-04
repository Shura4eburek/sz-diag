$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Поднять окно NVIDIA App поверх игры и снять экран (СЗ 123456).
#
# Зачем: настройки профиля драйвера для игры (Low Latency Mode, V-Sync, DLSS Override → Frame
# Generation) читаются только глазами — база nvdrsdb0.bin бинарная, CLI-экспорта нет.
#
#   szcli exec 123456 -f tools\recipes\client\nvapp-open.ps1 --in-session
#   szcli pull 123456 "C:\Windows\Temp\szdiag-screen.png"

$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    public const int RESTORE = 9;
}
'@

$exe = Get-ChildItem 'C:\Program Files\NVIDIA Corporation\NVIDIA App' -Recurse -Filter 'NVIDIA App.exe' -Depth 3 |
       Select-Object -First 1
'   exe: {0}' -f $exe.FullName

$p = Get-Process 'NVIDIA App' | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) {
    Start-Process $exe.FullName
    Start-Sleep -Seconds 12
    $p = Get-Process 'NVIDIA App' | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}
if ($p) {
    [void][Win]::ShowWindow($p.MainWindowHandle, [Win]::RESTORE)
    [void][Win]::SetForegroundWindow($p.MainWindowHandle)
    '   окно поднято: pid {0} «{1}»' -f $p.Id, $p.MainWindowTitle
} else {
    '   окно NVIDIA App не найдено'
}
Start-Sleep -Seconds 3

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save('C:\Windows\Temp\szdiag-screen.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
'   снимок сделан ({0}x{1})' -f $b.Width, $b.Height
