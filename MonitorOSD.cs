// ============================================================================
//  显示器 OSD 控制台 (MonitorOSD)
//  通过 VESA DDC/CI (MCCS) 协议在 Windows 里直接控制显示器 OSD 菜单,
//  不再需要按显示器上的物理按键。
//
//  编译: 运行 build.bat (使用 Windows 自带的 .NET Framework 编译器, 零依赖)
//  原理: dxva2.dll -> GetVCPFeatureAndVCPFeatureReply / SetVCPFeature
//        启动时读取显示器能力串(Capabilities String), 按显示器实际支持的
//        控制项动态生成界面。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MonitorOSD
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try { NativeMethods.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ------------------------------------------------------------------ Win32
    internal static class NativeMethods
    {
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmOrientation;
            public int dmPaperSize;
            public int dmPaperLength;
            public int dmPaperWidth;
            public int dmScale;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, ref uint length);

        // 文档声明为 LPWSTR, 但部分显示器驱动实际返回 ANSI 字节, 故用字节缓冲自行嗅探
        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] byte[] buffer, uint lengthInChars);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, ref uint vcpType, ref uint current, ref uint maximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint value);
    }

    // ----------------------------------------------------------- 显示器设备
    internal class MonitorDevice
    {
        public IntPtr Handle;
        public string Description = "";
        public string DeviceName = "";
        public Rectangle Bounds;
        public string CapsRaw = "";
        public string PanelType = "";
        public string MccsVersion = "";
        public Dictionary<byte, List<uint>> Vcp = new Dictionary<byte, List<uint>>();

        public string Title
        {
            get
            {
                string name = Description;
                if (name == null || name.Length == 0) name = "显示器";
                return name + "  [" + DeviceName + ", " + Bounds.Width + "×" + Bounds.Height + "]";
            }
        }
    }

    // -------------------------------------------------------------- 能力串解析
    internal static class CapsParser
    {
        private static readonly string[] KnownKeys = new string[] {
            "prot", "type", "model", "cmds", "mccs_ver", "mccs", "mswhql", "mslu",
            "vcp", "vcp_p10", "asset_eep", "mpu", "syn", "edid", "vfctl"
        };

        public static void Parse(MonitorDevice dev)
        {
            string caps = dev.CapsRaw;
            if (caps == null || caps.Length == 0) return;
            string s = caps.Trim();
            if (s.StartsWith("(")) s = s.Substring(1);
            if (s.EndsWith(")") && s.Length > 0) s = s.Substring(0, s.Length - 1);

            int i = 0, n = s.Length;
            while (i < n)
            {
                while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
                if (i >= n) break;
                int keyStart = i;
                while (i < n && s[i] != '(' && s[i] != ' ' && s[i] != ')') i++;
                string rawKey = s.Substring(keyStart, i - keyStart);
                string value = "";
                if (i < n && s[i] == '(')
                {
                    i++;
                    int vStart = i, depth = 0;
                    while (i < n)
                    {
                        char c = s[i];
                        if (c == '(') depth++;
                        else if (c == ')') { if (depth == 0) break; depth--; }
                        i++;
                    }
                    value = s.Substring(vStart, i - vStart);
                    if (i < n) i++;
                }

                string key = rawKey.ToLowerInvariant();
                // 部分固件把芯片名和键名粘连, 例如 "MStarcmds"
                if (Array.IndexOf(KnownKeys, key) < 0)
                {
                    foreach (string k in KnownKeys)
                    {
                        if (key.EndsWith(k) && key.Length > k.Length) { key = k; break; }
                    }
                }

                if (key == "vcp") ParseVcp(dev, value);
                else if (key == "mccs_ver" || key == "mccs") dev.MccsVersion = value.Trim();
                else if (key == "type") dev.PanelType = value;
                else if (key == "model" && value.Trim().Length > 0) dev.Description = value.Trim();
            }
        }

        // 解析 vcp 列表: "02 04 10 60(11 12 0F) AA(01 02) ..."
        private static void ParseVcp(MonitorDevice dev, string text)
        {
            int i = 0, n = text.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) break;
                int start = i;
                while (i < n && text[i] != '(' && !char.IsWhiteSpace(text[i])) i++;
                string codeStr = text.Substring(start, i - start);
                byte code;
                if (!byte.TryParse(codeStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) continue;

                List<uint> values = new List<uint>();
                if (i < n && text[i] == '(')
                {
                    i++;
                    int vStart = i, depth = 0;
                    while (i < n)
                    {
                        char c = text[i];
                        if (c == '(') depth++;
                        else if (c == ')') { if (depth == 0) break; depth--; }
                        i++;
                    }
                    string inner = text.Substring(vStart, i - vStart);
                    if (i < n) i++;
                    string[] toks = inner.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string t in toks)
                    {
                        uint v;
                        if (uint.TryParse(t.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                            values.Add(v);
                    }
                }
                dev.Vcp[code] = values;
            }
        }
    }

    // -------------------------------------------------------------- 枚举显示器
    internal static class MonitorEnumerator
    {
        public static List<MonitorDevice> Enumerate(out string error)
        {
            error = null;
            List<MonitorDevice> list = new List<MonitorDevice>();
            List<IntPtr> hmons = new List<IntPtr>();
            NativeMethods.MonitorEnumProc cb = delegate(IntPtr h, IntPtr hdc, ref NativeMethods.RECT r, IntPtr d)
            {
                hmons.Add(h);
                return true;
            };
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

            foreach (IntPtr hm in hmons)
            {
                NativeMethods.MONITORINFOEX mi = new NativeMethods.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
                if (!NativeMethods.GetMonitorInfo(hm, ref mi)) continue;

                uint count = 0;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, ref count) || count == 0) continue;
                NativeMethods.PHYSICAL_MONITOR[] arr = new NativeMethods.PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hm, count, arr)) continue;

                for (int i = 0; i < arr.Length; i++)
                {
                    MonitorDevice dev = new MonitorDevice();
                    dev.Handle = arr[i].hPhysicalMonitor;
                    dev.Description = arr[i].szPhysicalMonitorDescription == null ? "" : arr[i].szPhysicalMonitorDescription;
                    dev.DeviceName = mi.szDevice == null ? "" : mi.szDevice;
                    dev.Bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
                    dev.CapsRaw = ReadCaps(dev.Handle) ?? "";
                    CapsParser.Parse(dev);
                    list.Add(dev);
                }
            }
            return list;
        }

        private static string ReadCaps(IntPtr h)
        {
            uint len = 0;
            if (!NativeMethods.GetCapabilitiesStringLength(h, ref len) || len == 0) return null;
            byte[] buf = new byte[65536];
            uint chars = (uint)(buf.Length / 2);
            if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(h, buf, chars)) return null;
            if (buf[0] != 0 && buf[1] == 0)
            {
                // 真 UTF-16
                int bytes = (int)Math.Min(len * 2, (uint)buf.Length);
                return Encoding.Unicode.GetString(buf, 0, bytes);
            }
            // ANSI (本机显示器驱动即为此情况)
            int end = Array.IndexOf(buf, (byte)0);
            if (end < 0 || end > (int)len) end = (int)len;
            return Encoding.ASCII.GetString(buf, 0, end);
        }
    }

    // ------------------------------------------------------------------ 名称表
    internal class UIntName
    {
        public uint Value;
        public string Text;
        public UIntName(uint value, string text) { Value = value; Text = text; }
        public override string ToString() { return Text; }
    }

    internal static class VcpNames
    {
        private static readonly Dictionary<byte, string> CodeNames = new Dictionary<byte, string>
        {
            { 0x02, "新控制值" },
            { 0x04, "恢复出厂设置" },
            { 0x05, "恢复亮度/对比度默认" },
            { 0x06, "恢复几何默认" },
            { 0x08, "恢复颜色默认" },
            { 0x10, "亮度" },
            { 0x12, "对比度" },
            { 0x14, "色彩模式" },
            { 0x16, "红色增益" },
            { 0x18, "绿色增益" },
            { 0x1A, "蓝色增益" },
            { 0x1E, "自动校正" },
            { 0x3E, "色温" },
            { 0x52, "动态背光" },
            { 0x60, "输入源" },
            { 0x62, "扬声器音量" },
            { 0x6C, "黑电平 红" },
            { 0x6E, "黑电平 绿" },
            { 0x70, "黑电平 蓝" },
            { 0x87, "锐度" },
            { 0x8D, "静音" },
            { 0xA2, "自动颜色" },
            { 0xAA, "屏幕方向" },
            { 0xAC, "行频率" },
            { 0xAE, "刷新率" },
            { 0xB2, "锐度 (厂商)" },
            { 0xB6, "电源模式 (旧)" },
            { 0xC0, "累计使用时长" },
            { 0xC6, "应用控制键" },
            { 0xC8, "控制器型号" },
            { 0xC9, "固件版本" },
            { 0xCA, "OSD/按键控制" },
            { 0xD6, "电源模式" },
            { 0xDC, "画面模式" },
            { 0xDF, "VCP 版本" },
            { 0xFD, "厂商功能 0xFD" },
        };

        public static string Name(byte code)
        {
            string s;
            if (CodeNames.TryGetValue(code, out s)) return s;
            return "VCP 0x" + code.ToString("X2");
        }

        public static string Value(byte code, uint v)
        {
            switch (code)
            {
                case 0x60: return InputName(v);
                case 0xAA: return OrientName(v);
                case 0xB6:
                case 0xD6: return PowerName(v);
                case 0x8D: return v == 1 ? "静音" : (v == 2 ? "取消静音" : v.ToString());
                case 0x14:
                case 0x3E: return ColorPresetName(v);
                case 0xDC: return DisplayModeName(v);
                default: return v.ToString();
            }
        }

        private static string InputName(uint v)
        {
            switch (v)
            {
                case 0x01: return "VGA-1";
                case 0x02: return "VGA-2";
                case 0x03: return "DVI-1";
                case 0x04: return "DVI-2";
                case 0x05: return "AV-1";
                case 0x06: return "AV-2";
                case 0x07: return "S-Video-1";
                case 0x08: return "S-Video-2";
                case 0x09: return "分量-1";
                case 0x0A: return "分量-2";
                case 0x0F: return "DP-1";
                case 0x10: return "DP-2";
                case 0x11: return "HDMI-1";
                case 0x12: return "HDMI-2";
                case 0x13: return "HDMI-3";
                case 0x14: return "HDMI-4";
                case 0x90: return "USB-C";
                default: return "未知 0x" + v.ToString("X2");
            }
        }

        private static string OrientName(uint v)
        {
            switch (v)
            {
                case 1: return "0° (正常)";
                case 2: return "90°";
                case 3: return "180°";
                case 4: return "270°";
                default: return "0x" + v.ToString("X2");
            }
        }

        private static string PowerName(uint v)
        {
            switch (v)
            {
                case 1: return "开机";
                case 2: return "待机";
                case 3: return "挂起";
                case 4: return "关闭 (动鼠标唤醒)";
                case 5: return "关闭 (软)";
                default: return "0x" + v.ToString("X2");
            }
        }

        private static string ColorPresetName(uint v)
        {
            switch (v)
            {
                case 0x01: return "sRGB";
                case 0x02: return "面板原生";
                case 0x03: return "4000K";
                case 0x04: return "5000K";
                case 0x05: return "暖色 6500K";
                case 0x06: return "7500K";
                case 0x07: return "8200K";
                case 0x08: return "冷色 9300K";
                case 0x09: return "10000K";
                case 0x0A: return "11500K";
                case 0x0B: return "用户模式 1";
                case 0x0C: return "用户模式 2";
                case 0x0D: return "用户模式 3";
                default: return "未知 0x" + v.ToString("X2");
            }
        }

        private static string DisplayModeName(uint v)
        {
            switch (v)
            {
                case 0x00: return "标准";
                case 0x01: return "效率";
                case 0x02: return "影院";
                case 0x03: return "游戏 (FPS)";
                case 0x04: return "阅读";
                case 0x05: return "竞速 (RTS)";
                default: return "未知 0x" + v.ToString("X2");
            }
        }
    }

    // ------------------------------------------------------------------ 主窗体
    internal class VcpRow
    {
        public const int KindSlider = 0;
        public const int KindCombo = 1;
        public const int KindButton = 2;
        public const int KindInfo = 3;

        public byte Code;
        public string Name;
        public int Kind;
        public uint Current;
        public uint Max = 100;
        public List<UIntName> Options;
        public TrackBar Bar;
        public ComboBox Combo;
        public Label ValueLabel;
        public System.Windows.Forms.Timer SendTimer;
    }

    internal class MainForm : Form
    {
        private static readonly byte[] SectionCommon = new byte[] { 0x10, 0x12, 0x62, 0x8D, 0x60, 0xDC, 0x14, 0xAA, 0xD6 };
        private static readonly byte[] SectionImage = new byte[] { 0xB2, 0x87, 0x16, 0x18, 0x1A, 0x6C, 0x6E, 0x70, 0x3E, 0x52 };
        private static readonly byte[] SectionReset = new byte[] { 0x05, 0x08, 0x04, 0x06, 0x1E, 0xA2 };
        private static readonly byte[] SectionInfo = new byte[] { 0xAE, 0xAC, 0xC9, 0xDF, 0xC8, 0xC6, 0xC0, 0xFD, 0xB6 };

        private static readonly HashSet<byte> ReadOnlyCodes = new HashSet<byte>(
            new byte[] { 0x02, 0xAC, 0xAE, 0xC0, 0xC8, 0xC9, 0xDF, 0xB6, 0xC6 });
        private static readonly HashSet<byte> MomentaryCodes = new HashSet<byte>(
            new byte[] { 0x04, 0x05, 0x06, 0x08, 0x1E, 0xA2 });
        private static readonly HashSet<byte> SafeSliderCodes = new HashSet<byte>(
            new byte[] { 0x10, 0x12, 0x16, 0x18, 0x1A, 0x62, 0x6C, 0x6E, 0x70, 0x87, 0xB2, 0x3E });
        // 未写入能力串但实测可用的码 (全量扫描发现): 音量/静音/黑电平/色温
        private static readonly HashSet<byte> ExtraPresentCodes = new HashSet<byte>(
            new byte[] { 0x62, 0x8D, 0x6C, 0x6E, 0x70, 0x3E });

        private readonly object _ddcLock = new object();
        private List<MonitorDevice> _monitors = new List<MonitorDevice>();
        private MonitorDevice _current;
        private readonly List<VcpRow> _rows = new List<VcpRow>();
        private int _gen;
        private bool _suppress;
        private bool _closing;
        private Font _boldFont;

        private ComboBox _cboMonitor;
        private TableLayoutPanel _table;
        private Panel _scroller;
        private Label _status;
        private TextBox _capsBox;

        public MainForm()
        {
            Text = "显示器 OSD 控制台 (DDC/CI)";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 500);
            Size = new Size(780, 660);
            try { Font = new Font("Microsoft YaHei UI", 9F); } catch { }
            _boldFont = new Font(Font, FontStyle.Bold);

            // 顶部工具条
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 42;
            top.Padding = new Padding(10, 8, 10, 0);
            top.WrapContents = false;
            Label lbl = new Label();
            lbl.Text = "显示器:";
            lbl.AutoSize = true;
            lbl.Margin = new Padding(3, 9, 3, 0);
            _cboMonitor = new ComboBox();
            _cboMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboMonitor.Width = 360;
            _cboMonitor.Margin = new Padding(3, 5, 3, 0);
            Button btnReload = new Button();
            btnReload.Text = "重新枚举";
            btnReload.AutoSize = true;
            btnReload.Margin = new Padding(6, 4, 3, 0);
            btnReload.Click += delegate { ReloadMonitors(); };
            Button btnRefresh = new Button();
            btnRefresh.Text = "重读数值";
            btnRefresh.AutoSize = true;
            btnRefresh.Margin = new Padding(3, 4, 3, 0);
            btnRefresh.Click += delegate { RefreshValues(); };
            Button btnRestoreAll = new Button();
            btnRestoreAll.Text = "一键恢复全部";
            btnRestoreAll.AutoSize = true;
            btnRestoreAll.Margin = new Padding(6, 4, 3, 0);
            btnRestoreAll.Click += delegate { RestoreAll(); };
            _cboMonitor.SelectedIndexChanged += delegate
            {
                if (_suppress) return;
                SelectMonitor(_cboMonitor.SelectedIndex);
            };
            top.Controls.Add(lbl);
            top.Controls.Add(_cboMonitor);
            top.Controls.Add(btnReload);
            top.Controls.Add(btnRefresh);
            top.Controls.Add(btnRestoreAll);

            // 控件列表 (滚动区 + 表格)
            _table = new TableLayoutPanel();
            _table.Dock = DockStyle.Top;
            _table.AutoSize = true;
            _table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _table.ColumnCount = 3;
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _table.Padding = new Padding(8, 2, 8, 10);

            _scroller = new Panel();
            _scroller.Dock = DockStyle.Fill;
            _scroller.AutoScroll = true;
            _scroller.Controls.Add(_table);

            // 能力串详情
            _capsBox = new TextBox();
            _capsBox.Dock = DockStyle.Bottom;
            _capsBox.Multiline = true;
            _capsBox.ReadOnly = true;
            _capsBox.ScrollBars = ScrollBars.Both;
            _capsBox.WordWrap = false;
            _capsBox.Height = 0;
            _capsBox.Font = new Font("Consolas", 9F);

            // 底部状态条
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 36;
            bottom.ColumnCount = 3;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            _status = new Label();
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(10, 0, 0, 0);
            _status.Text = "初始化…";
            Button btnScan = new Button();
            btnScan.Dock = DockStyle.Fill;
            btnScan.Text = "扫描隐藏参数";
            btnScan.Margin = new Padding(4, 5, 2, 5);
            btnScan.Click += delegate { ScanHidden(btnScan); };
            Button btnCaps = new Button();
            btnCaps.Dock = DockStyle.Fill;
            btnCaps.Text = "能力串详情";
            btnCaps.Margin = new Padding(2, 5, 8, 5);
            btnCaps.Click += delegate
            {
                _capsBox.Height = _capsBox.Height > 0 ? 0 : 150;
            };
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(btnScan, 1, 0);
            bottom.Controls.Add(btnCaps, 2, 0);

            Controls.Add(_scroller);
            Controls.Add(_capsBox);
            Controls.Add(bottom);
            Controls.Add(top);

            Shown += delegate { ReloadMonitors(); };
            FormClosing += delegate
            {
                _closing = true;
                foreach (MonitorDevice m in _monitors)
                {
                    try { NativeMethods.DestroyPhysicalMonitor(m.Handle); } catch { }
                }
            };
        }

        // ------------------------------------------------------------- 基础设施
        private void RunBackground(System.Threading.ThreadStart work)
        {
            System.Threading.Thread t = new System.Threading.Thread(work);
            t.IsBackground = true;
            t.Start();
        }

        private void SafeUI(MethodInvoker mi)
        {
            if (_closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(mi); } catch { }
        }

        private void SetStatus(string text, int kind)
        {
            _status.Text = text;
            if (kind == 1) _status.ForeColor = Color.FromArgb(0, 128, 0);
            else if (kind == 2) _status.ForeColor = Color.Firebrick;
            else _status.ForeColor = Color.FromArgb(64, 64, 64);
        }

        // ------------------------------------------------------------- 枚举流程
        private void ReloadMonitors()
        {
            int gen = ++_gen;
            ClearRows();
            SetStatus("正在枚举显示器并读取能力串 (最多几秒)…", 0);
            List<MonitorDevice> old = _monitors;
            _monitors = new List<MonitorDevice>();
            _current = null;
            _capsBox.Text = "";
            _cboMonitor.Items.Clear();

            RunBackground(delegate
            {
                if (gen != _gen) return;
                foreach (MonitorDevice m in old)
                {
                    try { NativeMethods.DestroyPhysicalMonitor(m.Handle); } catch { }
                }
                string err = null;
                List<MonitorDevice> list = null;
                try { list = MonitorEnumerator.Enumerate(out err); }
                catch (Exception ex) { err = ex.Message; }

                SafeUI(delegate
                {
                    if (gen != _gen || _closing) return;
                    _monitors = list ?? new List<MonitorDevice>();
                    foreach (MonitorDevice m in _monitors) _cboMonitor.Items.Add(m.Title);
                    if (_monitors.Count == 0)
                    {
                        SetStatus("未发现可控制的物理显示器。" + (err ?? ""), 2);
                        return;
                    }
                    _suppress = true;
                    _cboMonitor.SelectedIndex = 0;
                    _suppress = false;
                    SelectMonitor(0);
                });
            });
        }

        private void SelectMonitor(int idx)
        {
            if (idx < 0 || idx >= _monitors.Count) return;
            _current = _monitors[idx];
            ClearRows();
            BuildRowsAsync(_current);
        }

        private void ClearRows()
        {
            foreach (VcpRow row in _rows)
            {
                if (row.SendTimer != null) { try { row.SendTimer.Dispose(); } catch { } }
            }
            _rows.Clear();
            _table.Controls.Clear();
            _table.RowStyles.Clear();
            _table.RowCount = 0;
        }

        // ------------------------------------------------------------- 行构建
        private static int DecideKind(MonitorDevice dev, byte code, out List<UIntName> options)
        {
            options = null;
            List<uint> vals;
            if (!dev.Vcp.TryGetValue(code, out vals) || vals == null) vals = new List<uint>();

            if (ReadOnlyCodes.Contains(code)) return VcpRow.KindInfo;
            if (code == 0x8D && vals.Count < 2)
            {
                options = new List<UIntName>();
                options.Add(new UIntName(1, "静音"));
                options.Add(new UIntName(2, "取消静音"));
                return VcpRow.KindCombo;
            }
            if (vals.Count >= 2)
            {
                options = new List<UIntName>();
                foreach (uint v in vals) options.Add(new UIntName(v, VcpNames.Value(code, v)));
                return VcpRow.KindCombo;
            }
            if (vals.Count == 1 || MomentaryCodes.Contains(code)) return VcpRow.KindButton;
            if (SafeSliderCodes.Contains(code)) return VcpRow.KindSlider;
            return VcpRow.KindInfo;
        }

        // 带重试的 VCP 读取: 显示器对连续快速读取会限流, 失败时重试 2 次
        private bool ReadVcp(MonitorDevice dev, byte code, out uint type, out uint cur, out uint max)
        {
            type = 0; cur = 0; max = 0;
            if (dev == null || _closing) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                lock (_ddcLock)
                {
                    try
                    {
                        type = 0; cur = 0; max = 0;
                        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(dev.Handle, code, ref type, ref cur, ref max))
                            return true;
                    }
                    catch { }
                }
                if (attempt < 2) System.Threading.Thread.Sleep(130);
            }
            return false;
        }

        private VcpRow BuildVcpRow(MonitorDevice dev, byte code)
        {
            List<UIntName> options;
            int kind = DecideKind(dev, code, out options);
            VcpRow row = new VcpRow();
            row.Code = code;
            row.Name = VcpNames.Name(code);
            row.Kind = kind;
            row.Options = options;

            if (kind == VcpRow.KindButton) return row;

            uint type = 0, cur = 0, max = 0;
            bool ok = ReadVcp(dev, code, out type, out cur, out max);
            if (kind == VcpRow.KindSlider)
            {
                if (!ok) return null;
                row.Current = cur;
                row.Max = (max > 0 && max <= 1000) ? max : 100;
                return row;
            }
            row.Current = ok ? cur : uint.MaxValue;
            return row;
        }

        private void BuildRowsAsync(MonitorDevice dev)
        {
            int gen = _gen;
            RunBackground(delegate
            {
                if (gen != _gen || _closing) return;

                byte[][] sections = new byte[][] { SectionCommon, SectionImage, SectionReset, SectionInfo };
                string[] sectionTitles = new string[] { "常用", "图像与颜色", "恢复默认", "只读信息" };

                int total = 0;
                foreach (byte[] sec in sections)
                    foreach (byte c in sec)
                        if (IsPresent(dev, c)) total++;

                if (total == 0)
                {
                    SafeUI(delegate
                    {
                        SetStatus("显示器未上报任何可控参数。请在显示器 OSD 菜单里确认 DDC/CI 已开启, 或更换/直连线缆。", 2);
                    });
                    return;
                }

                int done = 0;
                byte[] sectionRestore = new byte[] { 0x05, 0x08, 0x00, 0x00 };
                string[] sectionRestoreText = new string[] { "恢复亮度对比度", "恢复颜色默认", null, null };
                for (int s = 0; s < sections.Length; s++)
                {
                    if (gen != _gen || _closing) return;
                    int si = s;
                    bool headerAdded = false;
                    foreach (byte code in sections[s])
                    {
                        if (!IsPresent(dev, code)) continue;
                        done++;
                        int d = done;
                        SafeUI(delegate { SetStatus("正在读取参数 " + d + "/" + total + " …", 0); });
                        VcpRow row = BuildVcpRow(dev, code);
                        if (row == null) continue;
                        SafeUI(delegate
                        {
                            if (gen != _gen || _closing) return;
                            if (!headerAdded) { AddHeader(sectionTitles[si], sectionRestore[si], sectionRestoreText[si]); headerAdded = true; }
                            AddRow(row);
                        });
                    }
                }

                SafeUI(delegate
                {
                    if (gen != _gen || _closing) return;
                    AddSystemDisplayRows(dev);
                    string sub = dev.PanelType != null && dev.PanelType.Length > 0 ? dev.PanelType.ToUpperInvariant() + "  " : "";
                    if (dev.MccsVersion != null && dev.MccsVersion.Length > 0) sub += "MCCS " + dev.MccsVersion + "  ";
                    sub += "共 " + _rows.Count + " 项控制";
                    SetStatus("就绪 — " + sub, 1);
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("== 原始能力串 (Capabilities String) ==");
                    sb.AppendLine(dev.CapsRaw.Length > 0 ? dev.CapsRaw : "(未取得)");
                    sb.AppendLine();
                    sb.AppendLine("设备: " + dev.Title + "   面板类型: " + dev.PanelType + "   MCCS: " + dev.MccsVersion);
                    _capsBox.Text = sb.ToString();
                });
            });
        }

        private static bool IsPresent(MonitorDevice dev, byte code)
        {
            if (dev.Vcp.Count == 0)
                return code == 0x10 || code == 0x12 || code == 0x60; // 无能力串时的兜底
            return dev.Vcp.ContainsKey(code) || ExtraPresentCodes.Contains(code);
        }

        // ------------------------------------------------------------- UI 行
        private void AddHeader(string title, byte restoreCode, string restoreLabel)
        {
            int idx = _table.RowCount;
            _table.RowCount = idx + 1;
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label h = new Label();
            h.Text = "── " + title;
            h.AutoSize = true;
            h.ForeColor = Color.FromArgb(0, 102, 180);
            h.Font = _boldFont;
            h.Margin = new Padding(4, 16, 4, 4);
            _table.Controls.Add(h, 0, idx);
            _table.SetColumnSpan(h, 2);
            if (restoreCode != 0)
            {
                Button rb = new Button();
                rb.Text = restoreLabel != null ? restoreLabel : "恢复本组";
                rb.AutoSize = true;
                rb.Margin = new Padding(4, 12, 4, 0);
                byte rc = restoreCode;
                rb.Click += delegate { ExecuteRestore(rc); };
                _table.Controls.Add(rb, 2, idx);
            }
        }

        // 分组一键恢复: 0x05=恢复亮度/对比度, 0x08=恢复颜色
        private void ExecuteRestore(byte code)
        {
            MonitorDevice dev = _current;
            SendSetCode(dev, code, 1);
            ScheduleRefresh(1000);
        }

        // 总的一键恢复: 依次执行所有可编辑分组的恢复
        private void RestoreAll()
        {
            if (_current == null) return;
            DialogResult dr = MessageBox.Show(this,
                "将依次执行:\n  · 恢复亮度/对比度出厂默认\n  · 恢复颜色默认 (RGB 增益/黑电平等)\n\n扬声器音量、输入源、画面模式、电源等不会被改动。\n\n确定继续吗?",
                "一键恢复全部", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            MonitorDevice dev = _current;
            int gen = _gen;
            RunBackground(delegate
            {
                if (gen != _gen || _closing) return;
                SafeUI(delegate { SetStatus("一键恢复 1/2: 亮度/对比度…", 0); });
                SendSetCode(dev, 0x05, 1);
                System.Threading.Thread.Sleep(700);
                if (gen != _gen || _closing) return;
                SafeUI(delegate { SetStatus("一键恢复 2/2: 颜色默认…", 0); });
                SendSetCode(dev, 0x08, 1);
                System.Threading.Thread.Sleep(400);
                SafeUI(delegate { if (gen == _gen) RefreshValues(); });
            });
        }

        private void SendSetCode(MonitorDevice dev, byte code, uint value)
        {
            if (dev == null || _closing) return;
            bool ok = false;
            int err = 0;
            lock (_ddcLock)
            {
                try
                {
                    ok = NativeMethods.SetVCPFeature(dev.Handle, code, value);
                    if (!ok) err = Marshal.GetLastWin32Error();
                }
                catch { }
            }
            string msg = ok
                ? "已发送: " + VcpNames.Name(code) + " → " + VcpNames.Value(code, value)
                : "设置失败 (Win32 错误码 " + err + "): " + VcpNames.Name(code);
            int kind = ok ? 1 : 2;
            SafeUI(delegate { SetStatus(msg, kind); });
        }

        private void ScheduleRefresh(int delayMs)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = delayMs;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                RefreshValues();
            };
            t.Start();
        }

        // 系统侧 (显卡) 分辨率信息 + 打开显示设置的入口
        private void AddSystemDisplayRows(MonitorDevice dev)
        {
            string text = "未知";
            try
            {
                NativeMethods.DEVMODE dm = new NativeMethods.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));
                if (NativeMethods.EnumDisplaySettings(dev.DeviceName, -1, ref dm) && dm.dmPelsWidth > 0)
                    text = dm.dmPelsWidth + "×" + dm.dmPelsHeight + " @ " + dm.dmDisplayFrequency + " Hz";
            }
            catch { }

            int idx = _table.RowCount;
            _table.RowCount = idx + 1;
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label name = new Label();
            name.Text = "系统分辨率 (显卡控制)";
            name.AutoSize = true;
            name.Margin = new Padding(8, 12, 4, 0);
            Label val = new Label();
            val.Text = text;
            val.AutoSize = true;
            val.ForeColor = Color.FromArgb(90, 90, 90);
            val.Margin = new Padding(6, 12, 4, 0);
            Button btn = new Button();
            btn.Text = "打开 Windows 显示设置";
            btn.AutoSize = true;
            btn.Margin = new Padding(4, 6, 4, 0);
            btn.Click += delegate
            {
                try { Process.Start("ms-settings:display"); } catch { }
            };
            _table.Controls.Add(name, 0, idx);
            _table.Controls.Add(val, 1, idx);
            _table.Controls.Add(btn, 2, idx);
        }

        // 全量扫描 0x00-0xFF, 找出能力串之外的隐藏响应码 (只读)
        private void ScanHidden(Control btn)
        {
            MonitorDevice dev = _current;
            if (dev == null) return;
            int gen = _gen;
            btn.Enabled = false;
            btn.Text = "扫描中…";
            SetStatus("正在全量扫描 VCP 0x00-0xFF (约 15-25 秒, 只读, 不修改任何值)…", 0);
            RunBackground(delegate
            {
                List<int[]> found = new List<int[]>();
                for (int c = 0; c <= 255; c++)
                {
                    if (gen != _gen || _closing) return;
                    uint type = 0, cur = 0, max = 0;
                    bool ok = false;
                    lock (_ddcLock)
                    {
                        try { ok = NativeMethods.GetVCPFeatureAndVCPFeatureReply(dev.Handle, (byte)c, ref type, ref cur, ref max); }
                        catch { }
                    }
                    if (ok) found.Add(new int[] { c, (int)type, (int)cur, (int)max });
                    System.Threading.Thread.Sleep(6);
                }
                SafeUI(delegate
                {
                    if (gen != _gen || _closing) return;
                    int hidden = AddHiddenSection(found);
                    btn.Text = "已扫描";
                    SetStatus("扫描完成: " + found.Count + " 个响应码, 其中 " + hidden + " 个此前未展示", 1);
                });
            });
        }

        private static bool IsInSections(byte c)
        {
            byte[][] sections = new byte[][] { SectionCommon, SectionImage, SectionReset, SectionInfo };
            foreach (byte[] sec in sections)
                if (Array.IndexOf(sec, c) >= 0) return true;
            return c == 0x02;
        }

        private int AddHiddenSection(List<int[]> found)
        {
            List<int[]> extra = new List<int[]>();
            foreach (int[] f in found)
                if (!IsInSections((byte)f[0])) extra.Add(f);
            if (extra.Count == 0) return 0;
            AddHeader("厂商隐藏参数 (只读)", 0, null);
            foreach (int[] f in extra)
                AddHiddenRow((byte)f[0], (uint)f[2], (uint)f[3], (uint)f[1]);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("== 全量扫描响应码 (0x00-0xFF, 共 " + found.Count + " 个) ==");
            foreach (int[] f in found) sb.Append(f[0].ToString("X2") + " ");
            sb.AppendLine();
            _capsBox.AppendText(sb.ToString());
            return extra.Count;
        }

        private void AddHiddenRow(byte code, uint cur, uint max, uint type)
        {
            int idx = _table.RowCount;
            _table.RowCount = idx + 1;
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label name = new Label();
            name.Text = VcpNames.Name(code) + " 0x" + code.ToString("X2");
            name.AutoSize = true;
            name.Margin = new Padding(8, 10, 4, 0);
            Label val = new Label();
            val.Text = "当前=" + cur + ", 最大=" + max + ", 类型=" + type;
            val.AutoSize = true;
            val.ForeColor = Color.FromArgb(90, 90, 90);
            val.Margin = new Padding(6, 10, 4, 0);
            _table.Controls.Add(name, 0, idx);
            _table.Controls.Add(val, 1, idx);
            _table.SetColumnSpan(val, 2);
            VcpRow row = new VcpRow();
            row.Code = code;
            row.Name = name.Text;
            row.Kind = VcpRow.KindInfo;
            row.ValueLabel = val;
            _rows.Add(row);
        }

        private void AddRow(VcpRow row)
        {
            int idx = _table.RowCount;
            _table.RowCount = idx + 1;
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label name = new Label();
            name.Text = row.Name;
            name.AutoSize = true;
            name.Margin = new Padding(8, 12, 4, 0);

            Label val = new Label();
            val.AutoSize = true;
            val.ForeColor = Color.FromArgb(90, 90, 90);
            val.Margin = new Padding(6, 12, 4, 0);
            row.ValueLabel = val;

            if (row.Kind == VcpRow.KindSlider)
            {
                TrackBar bar = new TrackBar();
                bar.Minimum = 0;
                bar.Maximum = (int)Math.Max(1, row.Max);
                bar.TickStyle = TickStyle.None;
                bar.AutoSize = false;
                bar.Height = 30;
                bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                bar.Margin = new Padding(4, 6, 4, 0);
                row.Bar = bar;
                row.SendTimer = new System.Windows.Forms.Timer();
                row.SendTimer.Interval = 260;
                row.SendTimer.Tick += delegate
                {
                    row.SendTimer.Stop();
                    SendSet(row, (uint)bar.Value);
                };
                bar.ValueChanged += delegate
                {
                    row.ValueLabel.Text = bar.Value + " / " + row.Max;
                    if (_suppress) return;
                    row.SendTimer.Stop();
                    row.SendTimer.Start();
                };
                _suppress = true;
                bar.Value = Clamp((int)row.Current, 0, bar.Maximum);
                _suppress = false;
                row.ValueLabel.Text = bar.Value + " / " + row.Max;
                _table.Controls.Add(name, 0, idx);
                _table.Controls.Add(bar, 1, idx);
                _table.Controls.Add(val, 2, idx);
            }
            else if (row.Kind == VcpRow.KindCombo)
            {
                ComboBox combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.Width = 240;
                combo.Margin = new Padding(4, 7, 4, 0);
                foreach (UIntName opt in row.Options) combo.Items.Add(opt);
                row.Combo = combo;
                combo.SelectedIndexChanged += delegate
                {
                    if (_suppress) return;
                    int i = combo.SelectedIndex;
                    if (i < 0 || i >= row.Options.Count) return;
                    uint v = row.Options[i].Value;
                    if (row.Code == 0x60)
                    {
                        DialogResult dr = MessageBox.Show(this,
                            "切换输入源后, 如果所选接口没有信号, 屏幕会黑屏, 需要用显示器按键切回。\n确定要切换到 [" +
                            row.Options[i].Text + "] 吗?",
                            "切换输入源", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (dr != DialogResult.Yes)
                        {
                            _suppress = true;
                            combo.SelectedIndex = IndexOfValue(row.Options, row.Current);
                            _suppress = false;
                            return;
                        }
                    }
                    row.Current = v;
                    row.ValueLabel.Text = VcpNames.Value(row.Code, v);
                    SendSet(row, v);
                };
                int cur = IndexOfValue(row.Options, row.Current);
                _suppress = true;
                combo.SelectedIndex = cur; // 可能是 -1
                _suppress = false;
                row.ValueLabel.Text = cur >= 0
                    ? VcpNames.Value(row.Code, row.Current)
                    : (row.Current == uint.MaxValue ? "读取失败" : "当前 0x" + row.Current.ToString("X2") + " (未列出)");
                _table.Controls.Add(name, 0, idx);
                _table.Controls.Add(combo, 1, idx);
                _table.Controls.Add(val, 2, idx);
            }
            else if (row.Kind == VcpRow.KindButton)
            {
                Button btn = new Button();
                btn.Text = "执行";
                btn.AutoSize = true;
                btn.Margin = new Padding(4, 5, 4, 0);
                btn.Click += delegate { ExecuteButton(row); };
                row.ValueLabel.Text = "";
                _table.Controls.Add(name, 0, idx);
                _table.Controls.Add(btn, 1, idx);
                _table.Controls.Add(val, 2, idx);
            }
            else
            {
                row.ValueLabel.Text = row.Current == uint.MaxValue ? "-" : FormatInfo(row.Code, row.Current);
                _table.Controls.Add(name, 0, idx);
                _table.Controls.Add(val, 1, idx);
                _table.SetColumnSpan(val, 2);
            }
            _rows.Add(row);
        }

        private void ExecuteButton(VcpRow row)
        {
            if (row.Code == 0x04)
            {
                DialogResult dr = MessageBox.Show(this,
                    "恢复出厂设置会清除显示器上的所有自定义设置。\n确定继续吗?",
                    "确认恢复出厂", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }
            uint v = (row.Options != null && row.Options.Count > 0) ? row.Options[0].Value : 1;
            SendSet(row, v);
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 900;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                RefreshValues();
            };
            t.Start();
        }

        // ------------------------------------------------------------- 读写
        private void SendSet(VcpRow row, uint value)
        {
            MonitorDevice dev = _current;
            if (dev == null || _closing) return;
            bool ok = false;
            int err = 0;
            lock (_ddcLock)
            {
                try
                {
                    ok = NativeMethods.SetVCPFeature(dev.Handle, row.Code, value);
                    if (!ok) err = Marshal.GetLastWin32Error();
                }
                catch (Exception ex)
                {
                    SetStatus("设置失败: " + ex.Message, 2);
                    return;
                }
            }
            if (ok) SetStatus("已发送: " + row.Name + " → " + VcpNames.Value(row.Code, value), 1);
            else SetStatus("设置失败 (Win32 错误码 " + err + "): " + row.Name, 2);
        }

        private void RefreshValues()
        {
            MonitorDevice dev = _current;
            if (dev == null) return;
            List<VcpRow> rows = new List<VcpRow>(_rows);
            int gen = _gen;
            SetStatus("正在重读数值…", 0);
            RunBackground(delegate
            {
                if (gen != _gen) return;
                foreach (VcpRow row in rows)
                {
                    if (gen != _gen || _closing) return;
                    if (row.Kind == VcpRow.KindButton) continue;
                    uint type = 0, cur = 0, max = 0;
                    bool ok = ReadVcp(dev, row.Code, out type, out cur, out max);
                    uint current = ok ? cur : uint.MaxValue;
                    SafeUI(delegate
                    {
                        if (gen != _gen || _closing) return;
                        if (row.Kind == VcpRow.KindSlider && ok)
                        {
                            _suppress = true;
                            row.Bar.Value = Clamp((int)current, row.Bar.Minimum, row.Bar.Maximum);
                            _suppress = false;
                            row.ValueLabel.Text = row.Bar.Value + " / " + row.Max;
                        }
                        else if (row.Kind == VcpRow.KindCombo)
                        {
                            if (!ok) { row.ValueLabel.Text = "读取失败"; return; }
                            int i = IndexOfValue(row.Options, current);
                            if (i >= 0)
                            {
                                _suppress = true;
                                row.Combo.SelectedIndex = i;
                                _suppress = false;
                                row.ValueLabel.Text = VcpNames.Value(row.Code, current);
                            }
                            else row.ValueLabel.Text = "当前 0x" + current.ToString("X2") + " (未列出)";
                        }
                        else if (row.Kind == VcpRow.KindInfo)
                        {
                            row.ValueLabel.Text = ok ? FormatInfo(row.Code, current) : "-";
                        }
                    });
                }
                SafeUI(delegate { if (gen == _gen) SetStatus("数值已重读", 1); });
            });
        }

        // ------------------------------------------------------------- 工具
        private static string FormatInfo(byte code, uint v)
        {
            if (code == 0xAE) return (v / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " Hz";
            if (code == 0xAC) return (v / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " kHz";
            if (code == 0xDF) return "0x" + v.ToString("X4") + (v == 0x201 ? " (2.1)" : "");
            if (code == 0xC0) return v + " (约小时)";
            return v.ToString();
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        private static int IndexOfValue(List<UIntName> opts, uint v)
        {
            if (opts == null) return -1;
            for (int i = 0; i < opts.Count; i++)
                if (opts[i].Value == v) return i;
            return -1;
        }
    }
}
