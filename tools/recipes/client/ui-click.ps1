$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Клик мышью по координатам экрана клиента + сразу скриншот результата (СЗ 123456).
#
# Зачем: часть настроек живёт только в GUI (панель NVIDIA / NVIDIA App), а прочитать их из
# реестра или файлов нельзя — база профилей драйвера бинарная. Навигация идёт вслепую по
# скриншотам, поэтому клик и снимок делаются одной командой.
#
# Грабля: параметры задаются ОБЫЧНЫМИ присваиваниями в начале файла, а не через param() —
# `szcli exec --param X=605` гасит строку `$X = ...` и подставляет своё значение поверх текста
# скрипта. С блоком param() подстановка превращается в `605 = ...` и падает
# InvalidLeftHandSide.
#
# Координаты берутся ПРЯМО СО СНИМКА этого же рецепта: и снимок, и клик делает один и тот же
# процесс без DPI-awareness, поэтому его система координат совпадает (на 2560x1440 при
# масштабе 125% это 2048x1152).
#
# Прокрутка: --param Scroll=-5 (вниз) в точке X,Y. Координаты клика — ФИЗИЧЕСКИЕ пиксели
# (2560x1440), а снимок отдаётся в виртуальных (2048x1152, масштаб 125%): координату со снимка
# делить на 1.25. На этом дважды промахнулись мимо строки списка.
#
# ВАЖНО: только через --in-session (в session 0 нет ни курсора, ни рабочего стола) и только
# по согласованию — на машине может сидеть человек.
#
#   szcli exec 123456 -f tools\recipes\client\ui-click.ps1 --in-session --param X=605 --param Y=445
#   szcli pull 123456 "C:\Windows\Temp\szdiag-screen.png"

$X = 0
$Y = 0
$Delay = 1500
$Scroll = 0

$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MouseHelper {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void Wheel(int x, int y, int clicks) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0800, 0, 0, (uint)(clicks * 120), IntPtr.Zero);
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

if ([int]$Scroll -ne 0) {
    [MouseHelper]::Wheel([int]$X, [int]$Y, [int]$Scroll)
    '   прокрутка {0} щелчков в точке {1},{2}' -f $Scroll, $X, $Y
    Start-Sleep -Milliseconds ([int]$Delay)
}
elseif ([int]$X -gt 0 -or [int]$Y -gt 0) {
    [MouseHelper]::Click([int]$X, [int]$Y)
    '   клик по {0},{1}' -f $X, $Y
    Start-Sleep -Milliseconds ([int]$Delay)
}

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$path = 'C:\Windows\Temp\szdiag-screen.png'
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
'   снимок: {0}  {1:N0} КБ  ({2}x{3})' -f $path, ((Get-Item $path).Length / 1KB), $b.Width, $b.Height
