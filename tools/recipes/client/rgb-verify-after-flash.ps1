# СЗ 163013: приёмка после прошивки контроллера подсветки.
# Было: 048D:57DB = ITE Upgrade Mode (BOOT). Должно стать: 048D:5711 composite, usage FF89/0010.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

Write-Output "=== УСТРОЙСТВА ITE СЕЙЧАС ==="
Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_048D' } |
  Select-Object Status,Class,FriendlyName,InstanceId | Format-Table -AutoSize | Out-String -Width 150

$src = @'
using System; using System.Runtime.InteropServices;
public class Hid2 {
  [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
  [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] public static extern int  HidP_GetCaps(IntPtr p, ref CAPS c);
  [DllImport("hid.dll", CharSet=CharSet.Unicode)] public static extern bool HidD_GetProductString(IntPtr h, byte[] b, int len);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct CAPS {
    public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength;
    public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices; }
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

Write-Output "=== ЧТО ОТДАЁТ КОНТРОЛЛЕР ПО HID ==="
$guid = '{4d1e55b2-f16f-11cf-88cb-001111000030}'
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID' -ErrorAction SilentlyContinue |
  Where-Object { $_.PSChildName -match 'VID_048D' } | ForEach-Object {
    $dev = $_.PSChildName.ToLower()
    Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
      $inst = $_.PSChildName.ToLower()
      $path = ('\' + '\?\hid#' + $dev + '#' + $inst + '#' + $guid)
      $h = [Hid2]::CreateFile($path, [uint32]0, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
      if ($h -ne [IntPtr]::new(-1)) {
        $b = New-Object byte[] 256
        $name = if ([Hid2]::HidD_GetProductString($h,$b,256)) { [Text.Encoding]::Unicode.GetString($b).Trim([char]0) } else { '?' }
        $pp = [IntPtr]::Zero; $caps = ''
        if ([Hid2]::HidD_GetPreparsedData($h, [ref]$pp)) {
          $c = New-Object Hid2+CAPS
          if ([Hid2]::HidP_GetCaps($pp, [ref]$c) -eq 0x110000) {
            $caps = "UP=0x{0:X4} U=0x{1:X4}  In={2} Out={3} Feat={4}" -f $c.UsagePage,$c.Usage,$c.InputReportByteLength,$c.OutputReportByteLength,$c.FeatureReportByteLength
          }
          [void][Hid2]::HidD_FreePreparsedData($pp)
        }
        "  $dev : '$name'  $caps"
        [void][Hid2]::CloseHandle($h)
      }
    }
  }

Write-Output "=== ВИДИТ ЛИ ЕГО ТЕПЕРЬ SignalRGB (плагин 0x5711 в билде есть) ==="
$log = Get-ChildItem 'C:\Users\*\AppData\Local\WhirlwindFX\SignalRgb\Logs\*.log' -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
  "лог: $($log.Name)"
  Get-Content $log.FullName | Where-Object { $_ -match '5711|Motherboard|Gigabyte|GIGABYTE' } | Select-Object -Last 10
}
