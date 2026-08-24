# СЗ 163013: контроллер подсветки отдаёт нештатный HID (048D:57DB, usage page FF57, usage 00DB)
# вместо привычных Gigabyte 5702/5711 (usage 00CC). Вопрос: это рабочая прошивка нового чипа
# или контроллер в аварийном/загрузочном режиме. Дискриминатор — HID caps: размеры репортов.
# У рабочих RGB Fusion-контроллеров feature/output report = 64 байта.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$src = @'
using System;
using System.Runtime.InteropServices;
public class Hid {
  [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
  [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref ATTR a);
  [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
  [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] public static extern int  HidP_GetCaps(IntPtr p, ref CAPS c);
  [DllImport("hid.dll", CharSet=CharSet.Unicode)] public static extern bool HidD_GetProductString(IntPtr h, byte[] b, int len);
  [DllImport("hid.dll", CharSet=CharSet.Unicode)] public static extern bool HidD_GetManufacturerString(IntPtr h, byte[] b, int len);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct ATTR { public int Size; public ushort Vid; public ushort Pid; public ushort Ver; }
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
Add-Type -TypeDefinition $src -ErrorAction Stop


# перебираем HID-интерфейсы через симлинки \?\hid#...
$paths = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID\VID_048D&PID_57DB' -ErrorAction SilentlyContinue |
  ForEach-Object {
    $inst = $_.PSChildName
    $dp = Get-ItemProperty "$($_.PSPath)\Device Parameters" -ErrorAction SilentlyContinue
    "\\?\hid#vid_048d&pid_57db#$($inst.ToLower())#{4d1e55b2-f16f-11cf-88cb-001111000030}"
  }
foreach ($p in $paths) {
  Write-Output "=== $p ==="
  $h = [Hid]::CreateFile($p, ([uint32]3221225472), 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
  if ($h -eq [IntPtr]::new(-1)) {
    $h = [Hid]::CreateFile($p, [uint32]0, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)   # без доступа к данным
    if ($h -eq [IntPtr]::new(-1)) { Write-Output "  открыть не удалось (err $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"; continue }
    Write-Output "  открыт в режиме 0 (метаданные)"
  }
  $a = New-Object Hid+ATTR; $a.Size = 8
  if ([Hid]::HidD_GetAttributes($h, [ref]$a)) { "  VID=0x{0:X4} PID=0x{1:X4} Version=0x{2:X4}" -f $a.Vid,$a.Pid,$a.Ver }
  $buf = New-Object byte[] 256
  if ([Hid]::HidD_GetManufacturerString($h,$buf,256)) { "  Manufacturer: $([Text.Encoding]::Unicode.GetString($buf).Trim([char]0))" }
  if ([Hid]::HidD_GetProductString($h,$buf,256))      { "  Product     : $([Text.Encoding]::Unicode.GetString($buf).Trim([char]0))" }
  $pp = [IntPtr]::Zero
  if ([Hid]::HidD_GetPreparsedData($h, [ref]$pp)) {
    $c = New-Object Hid+CAPS
    if ([Hid]::HidP_GetCaps($pp, [ref]$c) -eq 0x110000) {
      "  UsagePage=0x{0:X4} Usage=0x{1:X4}" -f $c.UsagePage,$c.Usage
      "  Input={0} Output={1} Feature={2} байт" -f $c.InputReportByteLength,$c.OutputReportByteLength,$c.FeatureReportByteLength
      "  (у рабочих RGB Fusion-контроллеров Output/Feature = 64 или 65)"
    }
    [void][Hid]::HidD_FreePreparsedData($pp)
  }
  [void][Hid]::CloseHandle($h)
}
