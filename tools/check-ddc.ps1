# DDC/CI probe: enumerate monitors, read capabilities string and common VCP values.
# ASCII only. Read-only - nothing is changed on the monitor.
$ErrorActionPreference = 'Continue'

$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace Recon {
  public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L; public int T; public int R; public int B; }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct PHYSICAL_MONITOR {
    public IntPtr h;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct MONITORINFOEX {
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
  }

  public static class Ddc {
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("dxva2.dll")] public static extern bool GetCapabilitiesStringLength(IntPtr h, ref uint len);
    [DllImport("dxva2.dll")] public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, [Out] byte[] s, uint len);

    // Some monitor drivers return ANSI bytes even though the API is documented as LPWSTR.
    // Oversized buffer + sniff: if byte[1]==0 treat as UTF-16, else ASCII.
    public static string GetCaps(IntPtr h) {
      uint len = 0;
      if (!GetCapabilitiesStringLength(h, ref len) || len == 0) return null;
      byte[] buf = new byte[65536];
      uint chars = (uint)(buf.Length / 2);
      if (!CapabilitiesRequestAndCapabilitiesReply(h, buf, chars)) return null;
      if (len >= 2 && buf[0] != 0 && buf[1] == 0) return Encoding.Unicode.GetString(buf, 0, (int)(len * 2));
      int end = Array.IndexOf(buf, (byte)0);
      if (end < 0) end = (int)len;
      if (end > (int)len) end = (int)len;
      return Encoding.ASCII.GetString(buf, 0, end);
    }
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

Write-Output "=== Enumerating display monitors ==="
$hmHandles = [Recon.Helper]::EnumAll()
Write-Output ("HMONITOR count: " + $hmHandles.Length)

foreach ($hm in $hmHandles) {
  $mi = New-Object Recon.MONITORINFOEX
  $mi.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][Recon.MONITORINFOEX])
  [void][Recon.Ddc]::GetMonitorInfo($hm, [ref]$mi)
  Write-Output ("=== HMONITOR 0x{0:X} device={1} rect=({2},{3})-({4},{5})" -f $hm.ToInt64(), $mi.szDevice, $mi.rcMonitor.L, $mi.rcMonitor.T, $mi.rcMonitor.R, $mi.rcMonitor.B)

  $cnt = [uint32]0
  if (-not [Recon.Ddc]::GetNumberOfPhysicalMonitorsFromHMONITOR($hm, [ref]$cnt)) { Write-Output "  GetNumberOfPhysicalMonitorsFromHMONITOR FAILED"; continue }
  if ($cnt -lt 1) { Write-Output "  no physical monitors"; continue }
  $arr = New-Object 'Recon.PHYSICAL_MONITOR[]' ([int]$cnt)
  if (-not [Recon.Ddc]::GetPhysicalMonitorsFromHMONITOR($hm, $cnt, $arr)) { Write-Output "  GetPhysicalMonitorsFromHMONITOR FAILED"; continue }

  for ($i = 0; $i -lt [int]$cnt; $i++) {
    $h = $arr[$i].h
    Write-Output ("  physical[{0}]: desc='{1}' handle=0x{2:X}" -f $i, $arr[$i].desc, $h.ToInt64())

    $len = [uint32]0
    $rawLen = 0
    if ([Recon.Ddc]::GetCapabilitiesStringLength($h, [ref]$len)) {
      Write-Output ("  caps string length: " + $len)
      $capsText = [Recon.Ddc]::GetCaps($h)
      if ($null -ne $capsText) {
        Write-Output ("  caps: " + $capsText)
      } else { Write-Output "  caps request FAILED" }
    } else {
      Write-Output "  GetCapabilitiesStringLength FAILED (DDC/CI not available?)"
    }

    foreach ($code in @(0x10,0x12,0x14,0x16,0x18,0x1A,0x60,0x62,0x87,0xAA,0xB2,0xB6,0xD6,0xAC,0xAE,0xC0,0xC9,0xDF)) {
      $t=[uint32]0; $cur=[uint32]0; $max=[uint32]0
      if ([Recon.Ddc]::GetVCPFeatureAndVCPFeatureReply($h, [byte]$code, [ref]$t, [ref]$cur, [ref]$max)) {
        Write-Output ("  vcp 0x{0:X2}: type={1} cur={2} max={3}" -f $code, $t, $cur, $max)
      } else {
        Write-Output ("  vcp 0x{0:X2}: read FAILED" -f $code)
      }
    }
    [void][Recon.Ddc]::DestroyPhysicalMonitor($h)
  }
}
Write-Output "=== done ==="
