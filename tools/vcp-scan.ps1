# Full DDC/CI VCP code sweep 0x00..0xFF (READ-ONLY, nothing is changed).
# Vendors often support codes not listed in the capabilities string.
$ErrorActionPreference = 'Continue'

$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace Sweep {
  public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L; public int T; public int R; public int B; }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct PHYSICAL_MONITOR {
    public IntPtr h;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc;
  }

  public static class Ddc {
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte vcp, ref uint type, ref uint cur, ref uint max);
  }

  public static class Helper {
    public static IntPtr[] EnumAll() {
      var list = new List<IntPtr>();
      MonitorEnumProc cb = delegate(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) { list.Add(h); return true; };
      Ddc.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
      return list.ToArray();
    }
  }
}
'@
Add-Type -TypeDefinition $src -Language CSharp

$hmHandles = [Sweep.Helper]::EnumAll()
if ($hmHandles.Count -eq 0) { Write-Output "no monitors"; exit 1 }

$hm = $hmHandles[0]
$cnt = [uint32]0
[void][Sweep.Ddc]::GetNumberOfPhysicalMonitorsFromHMONITOR($hm, [ref]$cnt)
$arr = New-Object 'Sweep.PHYSICAL_MONITOR[]' ([int]$cnt)
[void][Sweep.Ddc]::GetPhysicalMonitorsFromHMONITOR($hm, $cnt, $arr)
$h = $arr[0].h

Write-Output "=== Full VCP sweep on first physical monitor ==="
$found = New-Object System.Collections.ArrayList
for ($code = 0; $code -le 255; $code++) {
  $t=[uint32]0; $cur=[uint32]0; $max=[uint32]0
  if ([Sweep.Ddc]::GetVCPFeatureAndVCPFeatureReply($h, [byte]$code, [ref]$t, [ref]$cur, [ref]$max)) {
    $line = ("0x{0:X2}  type={1}  cur={2}  max={3}" -f $code, $t, $cur, $max)
    [void]$found.Add($line)
    Write-Output $line
  }
  Start-Sleep -Milliseconds 8
}
[void][Sweep.Ddc]::DestroyPhysicalMonitor($h)
Write-Output ("=== sweep done, responding codes: " + $found.Count + " / 256 ===")
