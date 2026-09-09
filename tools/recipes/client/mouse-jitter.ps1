# СЗ 162003: клиент говорит — мышь в красном/синем (USB 3.x) порту даёт лаг в CS,
# в чёрном (USB 2.0) нет. Нужен объективный дискриминатор вместо «на глаз»: измеряем
# интервалы между HID-репортами мыши через RawInput. У 1000 Гц мыши норма — ~1 мс;
# радиопомеха USB3 на 2.4 ГГц приёмнику беспроводной мыши даёт ДЫРЫ в десятки мс
# (пропущенные пакеты), которые в игре и ощущаются как рывок.
#
# ВАЖНО: гонять с --in-session (ввод идёт в сеанс пользователя, агент живёт в session 0),
# и во время замера НЕПРЕРЫВНО двигать мышью — иначе репортов просто нет.
#   szcli exec <СЗ> -f tools\recipes\client\mouse-jitter.ps1 --in-session --param Seconds=60
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$Seconds = 60   # ← сколько секунд писать (двигать мышь всё это время!)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class MouseProbe {
  [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE {
    public ushort UsagePage; public ushort Usage; public int Flags; public IntPtr Target;
  }
  [DllImport("user32.dll", SetLastError=true)]
  public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
  [DllImport("user32.dll")] public static extern int GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hsize);
  // CharSet.Unicode обязателен: без него строки уезжают как ANSI и CreateWindowExW
  // возвращает 0 с бессмысленным GetLastError (видели 203).
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
  [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG m, IntPtr hwnd, uint min, uint max, uint remove);
  [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
  [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
  [StructLayout(LayoutKind.Sequential)] public struct MSG {
    public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
    public uint time; public int x; public int y;
  }

  const int HWND_MESSAGE = -3;
  const uint WM_INPUT = 0x00FF;

  public static List<double> Collect(int seconds) {
    IntPtr hwnd = CreateWindowExW(0, "STATIC", "szdiag-mouse-probe", 0, 0, 0, 0, 0,
                                  new IntPtr(HWND_MESSAGE), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    if (hwnd == IntPtr.Zero) throw new Exception("окно не создалось: " + Marshal.GetLastWin32Error());

    var rid = new RAWINPUTDEVICE[1];
    rid[0].UsagePage = 0x01; rid[0].Usage = 0x02; // generic desktop / mouse
    rid[0].Flags = 0x00000100;                    // RIDEV_INPUTSINK — ловим и без фокуса
    rid[0].Target = hwnd;
    if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
      throw new Exception("RegisterRawInputDevices: " + Marshal.GetLastWin32Error());

    var gaps = new List<double>();
    var sw = Stopwatch.StartNew();
    double last = -1;
    MSG msg;
    while (sw.Elapsed.TotalSeconds < seconds) {
      while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1)) {
        if (msg.message == WM_INPUT) {
          double now = sw.Elapsed.TotalMilliseconds;
          if (last >= 0) gaps.Add(now - last);
          last = now;
        }
        TranslateMessage(ref msg);
        DispatchMessage(ref msg);
      }
      System.Threading.Thread.Sleep(0);
    }
    return gaps;
  }
}
'@

# какая мышь и на каком контроллере — чтобы в отчёте порт не перепутать
Write-Output '=== Мышь и её путь до контроллера ==='
Get-PnpDevice -PresentOnly -Class Mouse -ErrorAction SilentlyContinue | ForEach-Object {
  $cur = $_.InstanceId
  $chain = @()
  for ($i = 0; $i -lt 8; $i++) {
    try { $p = (Get-PnpDeviceProperty -InstanceId $cur -KeyName 'DEVPKEY_Device_Parent' -ErrorAction Stop).Data } catch { break }
    if (-not $p) { break }
    $d = Get-PnpDevice -InstanceId $p -ErrorAction SilentlyContinue
    $loc = try { (Get-PnpDeviceProperty -InstanceId $p -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction Stop).Data } catch { $null }
    $chain += ("{0}{1}" -f $(if ($d) { $d.FriendlyName } else { $p }), $(if ($loc) { " [$loc]" } else { '' }))
    if ($p -like 'PCI\*') { break }
    $cur = $p
  }
  Write-Output ("{0}`n    id = {1}`n    {2}" -f $_.FriendlyName, $_.InstanceId, ($chain -join "`n    <- "))
}

Write-Output ''
Write-Output ("=== Замер {0} с — ДВИГАЙ МЫШЬ НЕПРЕРЫВНО ===" -f $Seconds)
$gaps = [MouseProbe]::Collect($Seconds)
if ($gaps.Count -lt 100) {
  Write-Output ("репортов всего {0} — мышь не двигали или ввод не в этот сеанс; замер недостоверен" -f $gaps.Count)
  return
}

$sorted = $gaps | Sort-Object
function Pct($p) { $sorted[[math]::Min($sorted.Count - 1, [int][math]::Floor($sorted.Count * $p))] }
$avg = ($gaps | Measure-Object -Average).Average
$hz  = if ($avg -gt 0) { [math]::Round(1000 / $avg) } else { 0 }

Write-Output ("репортов: {0}  ≈ {1} Гц (средний интервал {2:N3} мс)" -f $gaps.Count, $hz, $avg)
Write-Output ("медиана {0:N3}  p95 {1:N3}  p99 {2:N3}  p99.9 {3:N3}  макс {4:N1} мс" -f (Pct 0.5), (Pct 0.95), (Pct 0.99), (Pct 0.999), $sorted[-1])

foreach ($t in 5, 8, 16, 33, 50, 100) {
  $n = ($gaps | Where-Object { $_ -gt $t }).Count
  Write-Output ("дыр > {0,3} мс: {1,5}  ({2:N3} % репортов, {3:N1} в секунду)" -f $t, $n, (100 * $n / $gaps.Count), ($n / $Seconds))
}

Write-Output ''
Write-Output 'Топ-15 самых больших дыр (мс):'
Write-Output (($sorted | Select-Object -Last 15 | ForEach-Object { '{0:N1}' -f $_ }) -join '  ')
Write-Output ''
Write-Output 'ЧТЕНИЕ: у здоровой связки дыры > 16 мс единичны. Десятки дыр > 16 мс в секунду'
Write-Output 'при 1000 Гц = реальные пропуски пакетов — это и есть «лаг» в игре.'
