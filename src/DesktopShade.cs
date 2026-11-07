// DesktopShade —— 双击桌面空白处隐藏/恢复所有桌面图标
// 原理：只 ShowWindow(SW_HIDE) 桌面图标列表 SysListView32（Progman > SHELLDLL_DefView > SysListView32）。
//       壁纸在另一棵子树（Progman > WorkerW > WPEDesktopDX11Window），任务栏是 Shell_TrayWnd，都不碰。
// 触发：WH_MOUSE_LL 低级鼠标钩子观察 WM_LBUTTONDBLCLK，且命中窗口的根必须是 Progman。
//       显示态下额外做 LVM_HITTEST，双击落在图标上时不动作（避免误伤打开文件）。
// 编译：Framework 4.0 的 csc.exe，只支持 C# 5 语法（无字符串内插、无 ?. 、out 变量需先声明）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyProductAttribute("DesktopShade")]
[assembly: System.Reflection.AssemblyTitleAttribute("DesktopShade")]
[assembly: System.Reflection.AssemblyVersionAttribute("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersionAttribute("1.1.0.0")]
[assembly: System.Reflection.AssemblyDescriptionAttribute("双击桌面空白处隐藏/恢复桌面图标")]

internal static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    // 必须显式指定 W 入口点：kernel32 只导出 GetModuleHandleW，按裸名解析会 EntryPointNotFound
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc cb, IntPtr hMod, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr addr, uint size, alloc_type type, page_prot protect);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr addr, uint size, free_type type);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr baseAddr, byte[] buf, int size, out int written);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddr, byte[] buf, int size, out int read);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    public delegate IntPtr HookProc(int nCode, IntPtr w, IntPtr l);

    [Flags] public enum alloc_type : uint { Commit = 0x1000, Reserve = 0x2000 }
    [Flags] public enum free_type : uint { Release = 0x8000 }
    [Flags] public enum page_prot : uint { ReadWrite = 0x04 }

    public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNORMAL = 1;
    public const uint GA_ROOT = 2;
    public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207;
    public const uint MOUSEEVENTF_RBUTTONDOWN = 0x8, MOUSEEVENTF_RBUTTONUP = 0x10;
    public const int WH_MOUSE_LL = 14;
    public const uint LVM_HITTEST = 0x1012;          // LVM_FIRST + 18
    public const uint LVM_GETITEMCOUNT = 0x1000;     // LVM_FIRST + 0
    public const uint LVM_GETITEMPOSITION = 0x100A;  // LVM_FIRST + 10，LPARAM = POINT*（目标进程内）
    // LVHITTESTINFO 布局：LONG x(0) LONG y(4) UINT flags(8) INT iItem(12) INT iSubItem(16)
    // 早期版本把 flags 当成 iItem 读过，导致"能否隐藏"变成按指针 Y 坐标的位决定。
    public const uint LVHT_NOWHERE = 0x1, LVHT_ONITEMICON = 0x2, LVHT_ONITEMLABEL = 0x4, LVHT_ONITEMSTATEICON = 0x8;
    public const uint LVHT_ONITEM = LVHT_ONITEMICON | LVHT_ONITEMLABEL | LVHT_ONITEMSTATEICON;
    public const uint SMTO_ABORTIFHUNG = 0x2;
    public const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr extraInfo; }
}

internal sealed class DesktopShade
{
    const string AppTitle = "DesktopShade";
    const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string RunValueName = "DesktopShade";

    static Mutex singleInstance;
    static NotifyIcon tray;
    static ContextMenuStrip menu;
    static ToolStripMenuItem miHide, miShow, miAuto, miStatus;
    static IntPtr hookId = IntPtr.Zero;
    static Native.HookProc hookProc;          // 必须持有引用，否则被 GC 回收后回调崩溃
    static IntPtr progman = IntPtr.Zero;
    static IntPtr defView = IntPtr.Zero;
    static IntPtr listView = IntPtr.Zero;
    static System.Windows.Forms.Timer sync;
    static ContextMenuStrip deskMenu;      // 隐藏态下我们自己弹的一层菜单（Win11 原生菜单要多点一次"显示更多选项"）
    static ToolStripMenuItem itTrayToggle;
    static bool swallowRightUp = false;    // 这次右键的 DOWN 已被吞掉，UP 也必须吞
    static bool bypassRightOnce = false;   // 用户选了"Windows 原生菜单"，下一次右键放行
    static long lastToggleMs = 0;
    static bool iconsVisible = true;
    static string appDir = "";
    static readonly object logGate = new object();
    static long dblClkSeen = 0;
    static DateTime configWritten = DateTime.MinValue;
    static EventWaitHandle exitHandle = null;
    static EventWaitHandle previewHandle = null;

    static string ConfigPath()
    {
        string dir = appDir.Length > 0 ? appDir : Path.GetTempPath();
        return Path.Combine(dir, "desktopshade.ini");
    }

    // 用户最常改的放最前面，程序自己记的放后面
    static readonly string[] HeadKeys = new string[] {
        "autostart", "logclean", "tray",
        "menu_show", "menu_native", "menu_tray", "menu_exit",
        "traymenu_state", "traymenu_hide", "traymenu_show", "traymenu_tip",
        "traymenu_auto", "traymenu_selfhide", "traymenu_quit" };
    static readonly string[] TailKeys = new string[] { "running", "oldeststart", "laststart" };

    // 托盘右键菜单七行的文字（第 1 行是前缀，程序会在后面拼"：显示中/已隐藏"）
    static readonly string[] TrayLabelKeys = new string[] {
        "traymenu_state", "traymenu_hide", "traymenu_show", "traymenu_tip",
        "traymenu_auto", "traymenu_selfhide", "traymenu_quit" };
    static readonly string[] TrayLabelDefaults = new string[] {
        "当前", "隐藏桌面图标", "显示桌面图标", "再点一次空白处也能恢复",
        "登录时自动启动", "隐藏这个托盘图标", "退出 DesktopShade" };
    static string[] trayLabels = null;
    static string[] TrayMenuLabels(Dictionary<string, string> cfg)
    {
        var l = new string[TrayLabelKeys.Length];
        for (int i = 0; i < TrayLabelKeys.Length; i++) l[i] = CfgText(cfg, TrayLabelKeys[i], TrayLabelDefaults[i]);
        return l;
    }

    static string CommentOf(string key)
    {
        if (key == "autostart") return "   # True=开机自启，False=不开机自启";
        if (key == "logclean") return "   # 日志清理间隔：数字+单位，如 3days / 10minutes / 1month（单复数都可）";
        if (key == "tray") return "   # 1=显示托盘图标，0=不显示（也可在隐藏态右键菜单里改）";
        return "";   // menu_* 的值允许含 # 号，所以不写行内注释，说明放在文件头
    }

    // 菜单文字允许含 # 号，所以这几个键不剥行内注释
    static bool RawValueKey(string key)
    {
        // 菜单文字允许含 # 号，所以这些键不剥行内注释
        return key.StartsWith("menu_") || key.StartsWith("traymenu_");
    }

    static string CfgText(Dictionary<string, string> cfg, string key, string fallback)
    {
        string v;
        if (cfg.TryGetValue(key, out v))
        {
            v = v.Trim();
            if (v.Length > 0) return v;
        }
        return fallback;
    }

    // 隐藏态那层菜单的四行文字；托盘那条不给自定义名时仍按当前状态显示"显示/隐藏"
    static string[] DeskMenuLabels(Dictionary<string, string> cfg, bool trayVisible)
    {
        var labels = new string[4];
        labels[0] = CfgText(cfg, "menu_show", "显示桌面图标");
        labels[1] = CfgText(cfg, "menu_native", "Windows 原生菜单（新建 / 刷新 / 个性化）");
        labels[2] = CfgText(cfg, "menu_tray",
            (trayVisible ? "隐藏托盘图标" : "显示托盘图标") + "（记住这个选择）");
        labels[3] = CfgText(cfg, "menu_exit", "退出 DesktopShade（下次登录仍会自启）");
        return labels;
    }

    static Dictionary<string, string> ReadConfig()
    {
        // 配置刚被另一个进程改写过时可能读到一半，重试三次；仍失败就返回空表，由调用方说明原因
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(ConfigPath())) return d;
                foreach (string rawLine in File.ReadAllLines(ConfigPath()))
                {
                    string line = rawLine.Trim();
                    // 整行注释必须先看首字符：注释里也可能有等号（比如 "留空 = 自动…"），否则会被当成配置项
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1);
                    if (!RawValueKey(k))
                    {
                        int hash = v.IndexOf('#');                            // 支持行内注释
                        if (hash >= 0) v = v.Substring(0, hash);
                    }
                    d[k] = v.Trim();
                }
                return d;
            }
            catch (Exception ex)
            {
                Log("读配置第 " + (attempt + 1) + " 次失败 " + ex.GetType().Name + ": " + ex.Message);
                Thread.Sleep(40);
            }
        }
        return new Dictionary<string, string>();
    }

    static bool ParseBool(string v, bool fallback)
    {
        if (string.IsNullOrEmpty(v)) return fallback;
        v = v.Trim().ToLowerInvariant();
        if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
        if (v == "0" || v == "false" || v == "no" || v == "off") return false;
        return fallback;
    }

    // 3days / 1day / 10minutes / 1month …… month 按 30 天、year 按 365 天近似
    static bool TryParseSpan(string v, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        if (string.IsNullOrEmpty(v)) return false;
        v = v.Trim().ToLowerInvariant().Replace(" ", "");
        int i = 0;
        while (i < v.Length && v[i] >= '0' && v[i] <= '9') i++;
        long n;
        if (i == 0 || !long.TryParse(v.Substring(0, i), out n) || n <= 0) return false;
        string unit = v.Substring(i);
        if (unit.EndsWith("s") && unit.Length > 1) unit = unit.Substring(0, unit.Length - 1);
        switch (unit)
        {
            case "second": span = TimeSpan.FromSeconds(n); return true;
            case "minute": span = TimeSpan.FromMinutes(n); return true;
            case "hour": span = TimeSpan.FromHours(n); return true;
            case "day": span = TimeSpan.FromDays(n); return true;
            case "week": span = TimeSpan.FromDays(n * 7); return true;
            case "month": span = TimeSpan.FromDays(n * 30); return true;
            case "year": span = TimeSpan.FromDays(n * 365); return true;
            default: return false;
        }
    }

    // 第一次运行时把用户键补齐，好让人一打开 ini 就看得见全部可改项
    static void SeedConfig(Dictionary<string, string> cfg)
    {
        if (!cfg.ContainsKey("autostart")) cfg["autostart"] = AutoStartOn() ? "True" : "False";
        if (!cfg.ContainsKey("logclean")) cfg["logclean"] = "3days";
        if (!cfg.ContainsKey("tray")) cfg["tray"] = "0";   // 默认不占托盘；想要图标改成 1 或走隐藏态右键菜单
        if (!cfg.ContainsKey("menu_show")) cfg["menu_show"] = "显示桌面图标";
        if (!cfg.ContainsKey("menu_native")) cfg["menu_native"] = "Windows 原生菜单（新建 / 刷新 / 个性化）";
        if (!cfg.ContainsKey("menu_tray")) cfg["menu_tray"] = "";
        if (!cfg.ContainsKey("menu_exit")) cfg["menu_exit"] = "退出 DesktopShade（下次登录仍会自启）";
        for (int i = 0; i < TrayLabelKeys.Length; i++)
            if (!cfg.ContainsKey(TrayLabelKeys[i])) cfg[TrayLabelKeys[i]] = TrayLabelDefaults[i];
    }

    // 自启以配置为准：改了 autostart 就去登记/注销注册表项，不需要重启程序
    static void ApplyAutostart(Dictionary<string, string> cfg)
    {
        bool want;
        string s;
        if (cfg.TryGetValue("autostart", out s)) want = ParseBool(s, AutoStartOn());
        else { want = AutoStartOn(); cfg["autostart"] = want ? "True" : "False"; }   // 第一次：按现状落盘
        bool actual = AutoStartOn();
        // 光看"登记没登记"不够：换目录或覆盖安装时，旧值指向的还是上一个 exe，
        // 那样重启后起来的是旧副本。所以要连路径一起比。
        bool pointsHere = AutoStartPointsHere();
        if (want == actual && (!want || pointsHere)) return;
        if (SetAutoStart(want)) Log("按配置 autostart=" + (want ? "True" : "False") + "，已" + (want ? "登记" : "注销") + "自启项"
                                    + (want && !pointsHere && actual ? "（原来指向别的位置，已改成当前 exe）" : ""));
        else Log("自启项" + (want ? "登记" : "注销") + "失败（注册表写入被拒？）");
        if (miAuto != null) miAuto.Checked = want;
    }

    static string OneLine(string s)
    {
        return s.Replace("\r", "").Replace("\n", "\\n");
    }

    static void SaveConfig(string key, string val)
    {
        var d = ReadConfig();
        d[key] = val;
        WriteConfig(d);
    }

    static void WriteConfig(Dictionary<string, string> d)
    {
        try
        {
            // 文件已存在就地换值，保留用户的空行/分组/注释；第一次才生成模板
            string text = File.Exists(ConfigPath()) ? MergeKeepingLayout(d) : DefaultConfigText(d);
            var enc = new UTF8Encoding(false);   // 不带 BOM，手改/别处解析都省事
            bool done = false;
            for (int attempt = 0; attempt < 3 && !done; attempt++)
            {
                try { File.WriteAllText(ConfigPath(), text, enc); done = true; }
                catch (IOException) { Thread.Sleep(40); }
            }
            Log("写配置 " + d.Count + " 个键" + (done ? "" : " —— 三次都没写进去"));
        }
        catch (Exception ex) { Log("写配置失败 " + ex.Message); }
    }

    static string DefaultConfigText(Dictionary<string, string> d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DesktopShade 设置：用记事本改就行，保存后程序半秒内自动跟上");
        sb.AppendLine("# 空行、分组、# 说明都可以随便加：程序写回时会原样保留你的排版，只换等号右边");
        sb.AppendLine("# menu_* = 隐藏桌面后右键那层菜单的字；traymenu_* = 托盘图标右键菜单的字；只改字不改功能");
        sb.AppendLine("# menu_* / traymenu_* 的值里别用 # 号（这几行不认行内注释）；menu_tray 留空=自动按状态取名");
        sb.AppendLine();
        foreach (string k in HeadKeys)
            if (d.ContainsKey(k)) sb.AppendLine(k + "=" + d[k] + CommentOf(k));
        sb.AppendLine();
        sb.AppendLine("# 以下由程序自己维护，不用动");
        foreach (string k in TailKeys) if (d.ContainsKey(k)) sb.AppendLine(k + "=" + d[k]);
        return sb.ToString();
    }

    // 就地替换：认出的 key=value 行换成新值（连行内注释一起保留），其它行一字不动
    static string MergeKeepingLayout(Dictionary<string, string> d)
    {
        var remaining = new Dictionary<string, string>(d);
        string[] rawLines;
        try { rawLines = File.ReadAllLines(ConfigPath()); }
        catch { return DefaultConfigText(d); }
        var sb = new StringBuilder();
        foreach (string rawLine in rawLines)
        {
            string outLine = rawLine;
            string t = rawLine.Trim();
            if (t.Length > 0 && t[0] != '#' && t[0] != ';')
            {
                int eq = t.IndexOf('=');
                if (eq > 0)
                {
                    string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                    string rest = t.Substring(eq + 1);
                    string tail = "";
                    if (!RawValueKey(k))
                    {
                        int h = rest.IndexOf('#');
                        if (h >= 0) { tail = "   " + rest.Substring(h).TrimEnd(); rest = rest.Substring(0, h); }
                    }
                    if (remaining.ContainsKey(k))
                    {
                        outLine = k + "=" + remaining[k] + tail;
                        remaining.Remove(k);
                    }
                }
            }
            sb.AppendLine(outLine);
        }
        if (remaining.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# （程序补上的键）");
            foreach (string k in HeadKeys)
                if (remaining.ContainsKey(k)) { sb.AppendLine(k + "=" + remaining[k] + CommentOf(k)); remaining.Remove(k); }
            foreach (KeyValuePair<string, string> kv in remaining) sb.AppendLine(kv.Key + "=" + kv.Value);
        }
        return sb.ToString();
    }

    // 按配置决定要不要托盘图标；常驻期间可以从命令行随时开关
    static void EnsureTray(bool want)
    {
        if (want && tray == null) BuildTray();
        else if (!want && tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
            tray = null;
            Log("按配置移除托盘图标");
        }
    }

    // 看不见的例行小务（只在常驻启动时跑一次，命令行动词不碰）：
    // 比较「本次启动」与「本轮最老一次启动」的时间差，够 3 天就删日志（含 .old）。
    // 关键点：窗口起点 oldeststart 只在清理时才重置，中间重启多少次都不刷新，
    // 所以哪怕你每天关机开机，跨到第 3 天照样会清（比"和上一次启动比"更合用意）。
    static void StartupLogMaintenance()
    {
        try
        {
            Dictionary<string, string> cfg = ReadConfig();
            long now = DateTime.Now.Ticks;
            string s;
            long oldest = 0, prev = 0;
            if (cfg.TryGetValue("oldeststart", out s)) long.TryParse(s, out oldest);
            if (cfg.TryGetValue("laststart", out s)) long.TryParse(s, out prev);
            bool firstRun = false;
            if (oldest <= 0) { oldest = (prev > 0 ? prev : now); firstRun = true; }   // 兼容只记过 laststart 的旧配置
            // 间隔来自配置里的 logclean（如 3days / 10minutes / 1month）；缺失或写错就退回 3days
            string limText;
            TimeSpan limit = TimeSpan.FromDays(3);      // 先给默认值，&& 短路时 out 参数可能未被赋值
            bool limitOk = cfg.TryGetValue("logclean", out limText) && TryParseSpan(limText, out limit);
            if (!limitOk) { limit = TimeSpan.FromDays(3); limText = "3days"; }
            string limitNote = "，清理间隔 " + limText + "（" + limit.TotalDays.ToString("0.###") + " 天）" +
                               (limitOk ? "" : "：配置缺失或写法不对，已按默认");
            double spanDays = TimeSpan.FromTicks(now - oldest).TotalDays;
            bool due = spanDays >= limit.TotalDays;
            long bytes = 0;
            if (due)
            {
                foreach (string p in new string[] { LogPath(), LogPath() + ".old" })
                {
                    try { if (File.Exists(p)) { bytes += new FileInfo(p).Length; File.Delete(p); } }
                    catch (Exception ex) { Log("删除 " + Path.GetFileName(p) + " 失败 " + ex.Message); }
                }
            }
            cfg["oldeststart"] = (due ? now : oldest).ToString();
            cfg["laststart"] = now.ToString();
            cfg.Remove("lastlogclean");        // 上一版键名，不留残留
            WriteConfig(cfg);
            if (due) Log("自动清理日志：删了 " + bytes + " 字节，本轮窗口跨 " + spanDays.ToString("0.0") + " 天，窗口重新起算" + limitNote);
            else if (firstRun) Log("记录启动窗口起点，本次不清理" + limitNote);
            else Log("本轮启动窗口已跨 " + spanDays.ToString("0.0") + " 天，不到设定值，不清理；距上次启动 " +
                     TimeSpan.FromTicks(now - (prev > 0 ? prev : now)).TotalDays.ToString("0.0") + " 天" + limitNote);
        }
        catch (Exception ex) { Log("启动维护出错 " + ex.GetType().Name + ": " + ex.Message); }
    }

    // 退出前把桌面图标放回来：隐藏只是运行期 ShowWindow，进程一走没人恢复，
    // 而 Windows 自己的"显示桌面图标"复选框并不知道，桌面会一片空白。
    static bool restoredOnExit = false;
    static void RestoreOnExit(object sender, EventArgs e)
    {
        if (restoredOnExit) return;
        restoredOnExit = true;
        try
        {
            if (Locate() && !Native.IsWindowVisible(listView))
            {
                Native.ShowWindow(listView, Native.SW_SHOWNORMAL);
                Log("退出前恢复桌面图标");
            }
        }
        catch (Exception ex) { Log("退出前恢复图标失败 " + ex.GetType().Name + ": " + ex.Message); }
        try
        {
            Dictionary<string, string> cfg = ReadConfig();
            cfg["running"] = "0";            // 干净退出，下次启动就不用兜底
            WriteConfig(cfg);
        }
        catch (Exception ex) { Log("清运行标记出错 " + ex.GetType().Name + ": " + ex.Message); }
    }

    // 只写配置，增删图标交给 400ms 心跳：省得在菜单项回调里把自己正在用的控件 Dispose 掉
    static void SetTrayWanted(bool want)
    {
        SaveConfig("tray", want ? "1" : "0");
        Log("菜单请求托盘=" + (want ? "显示" : "隐藏") + "，等下次心跳应用");
    }

    static string LogPath()
    {
        string dir = appDir.Length > 0 ? appDir : Path.GetTempPath();
        return Path.Combine(dir, "desktopshade.log");
    }

    static void Log(string s)
    {
        try
        {
            lock (logGate)
            {
                try
                {
                    // 到 256KB 就滚一代：磁盘占用封顶约 512KB，异常刷屏也不会吃硬盘
                    FileInfo lf = new FileInfo(LogPath());
                    if (lf.Exists && lf.Length > 262144)
                    {
                        string bak = LogPath() + ".old";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(LogPath(), bak);
                    }
                }
                catch { }
                using (StreamWriter w = new StreamWriter(LogPath(), true, Encoding.UTF8))
                {
                    w.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    w.Write("  ");
                    w.WriteLine(s);
                }
            }
        }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        try { appDir = Path.GetDirectoryName(Application.ExecutablePath); } catch { appDir = ""; }
        // 控制台默认按 OEM 代码页写，重定向时中文会变乱码；直接把 stdout 换成 UTF-8（OutputEncoding 在重定向下会静默失败）
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }); }
        catch { }

        // 选项类参数先处理并落盘，这样"已有实例在跑"时也能改配置（在跑的那个 400ms 内会自己跟上）
        int trayOverride = -1;
        foreach (string raw in args)
        {
            string a = raw.ToLowerInvariant();
            if (a == "--tray") trayOverride = 1;
            else if (a == "--notray") trayOverride = 0;
        }
        if (trayOverride >= 0) SaveConfig("tray", trayOverride == 1 ? "1" : "0");

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        bool owner;
        singleInstance = new Mutex(true, AppTitle + ".SingleInstance", out owner);
        bool isResident = !(cmd == "--hide" || cmd == "--show" || cmd == "--toggle" || cmd == "--status"
                            || cmd == "--probe" || cmd == "--icons" || cmd == "--exit" || cmd == "--preview"
                            || cmd == "--version"
                            || cmd == "--install-autostart" || cmd == "--uninstall-autostart");
        if (isResident && !owner)
        {
            Console.WriteLine("已有一个 DesktopShade 在运行，本次退出。");
            return 0;
        }

        if (!Locate()) { Console.WriteLine("找不到桌面图标窗口（Progman>SHELLDLL_DefView>SysListView32），放弃。"); return 2; }

        if (cmd == "--status") { PrintStatus(); return 0; }
        if (cmd == "--probe")
        {
            int px, py;
            if (args.Length < 3 || !int.TryParse(args[1], out px) || !int.TryParse(args[2], out py))
            { Console.WriteLine("PROBE_BADARGS（用法 --probe X Y）"); return 2; }
            Probe(px, py);
            return 0;
        }
        if (cmd == "--icons") { Icons(); return 0; }
        if (cmd == "--version")
        {
            Console.WriteLine("VERSION=" + typeof(DesktopShade).Assembly.GetName().Version.ToString(3));
            try
            {
                Console.WriteLine("PATH=" + Application.ExecutablePath);
                Console.WriteLine("BUILT=" + File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
            return 0;
        }
        if (cmd == "--exit")
        {
            try
            {
                using (EventWaitHandle h = new EventWaitHandle(false, EventResetMode.AutoReset, AppTitle + ".Exit"))
                {
                    h.Set();
                    Console.WriteLine("EXIT_SIGNALED（没有在跑的实例时什么都没发生）");
                }
            }
            catch (Exception ex) { Console.WriteLine("EXIT_FAIL " + ex.GetType().Name + " " + ex.Message); return 3; }
            return 0;
        }
        if (cmd == "--preview")
        {
            // 让在跑的实例把托盘菜单在光标处弹一次：验证/够不着托盘图标时用（会顺带把 tray 置 1）
            try
            {
                using (EventWaitHandle h = new EventWaitHandle(false, EventResetMode.AutoReset, AppTitle + ".Preview"))
                {
                    h.Set();
                    Console.WriteLine("PREVIEW_SIGNALED");
                }
            }
            catch (Exception ex) { Console.WriteLine("PREVIEW_FAIL " + ex.GetType().Name + " " + ex.Message); return 3; }
            return 0;
        }
        if (cmd == "--hide") { SetHidden(true); Console.WriteLine(iconsVisible ? "隐藏失败" : "已隐藏"); return 0; }
        if (cmd == "--show") { SetHidden(false); Console.WriteLine(iconsVisible ? "已显示" : "恢复失败"); return 0; }
        if (cmd == "--toggle") { SetHidden(iconsVisible); Console.WriteLine(iconsVisible ? "已显示" : "已隐藏"); return 0; }
        if (cmd == "--install-autostart" || cmd == "--uninstall-autostart")
        {
            // 走配置这条路，让 ini 始终是唯一事实来源（正在跑的实例会自动跟上）
            bool want = cmd == "--install-autostart";
            Dictionary<string, string> ca = ReadConfig();
            ca["autostart"] = want ? "True" : "False";
            WriteConfig(ca);
            ApplyAutostart(ca);
            Console.WriteLine((AutoStartOn() == want ? "OK " : "WARN ") + (want ? "自启已登记" : "自启已注销") +
                              "，配置 autostart=" + ca["autostart"]);
            return 0;
        }

        try
        {
            try
            {
                Dictionary<string, string> cfgSeed = ReadConfig();
                SeedConfig(cfgSeed);
                ApplyAutostart(cfgSeed);        // 配置里 autostart 说了算
                WriteConfig(cfgSeed);
            }
            catch (Exception ex) { Log("配置初始化出错 " + ex.GetType().Name + ": " + ex.Message); }
            StartupLogMaintenance();    // 只在启动时比一次窗口跨度
            // 上次是不是干净退出：不是的话图标可能被留在隐藏态，这里兜回来
            bool dirty = false, restoredNow = false;
            try
            {
                Dictionary<string, string> cfgR = ReadConfig();
                string flag;
                if (cfgR.TryGetValue("running", out flag)) dirty = (flag == "1");
                if (dirty && Locate() && !Native.IsWindowVisible(listView))
                {
                    Native.ShowWindow(listView, Native.SW_SHOWNORMAL);
                    restoredNow = true;
                }
                cfgR["running"] = "1";
                WriteConfig(cfgR);
                if (dirty) Log(restoredNow ? "上次不是干净退出，图标被留在隐藏态，已恢复显示"
                                           : "上次不是干净退出，但图标本来是显示态，无需恢复");
                else Log("记录运行标记（干净退出时会清掉）");
            }
            catch (Exception ex) { Log("运行标记处理出错 " + ex.GetType().Name + ": " + ex.Message); }
            bool wantTray = true;
            string why = "没有配置文件，默认显示";
            try
            {
                Dictionary<string, string> cfg0 = ReadConfig();
                if (cfg0.ContainsKey("tray")) { wantTray = cfg0["tray"] != "0"; why = "tray=" + cfg0["tray"]; }
                else if (File.Exists(ConfigPath())) why = "配置文件存在但没读到 tray 键，实际内容=<" + OneLine(File.ReadAllText(ConfigPath())) + ">";
                FileInfo fi0 = new FileInfo(ConfigPath());
                if (fi0.Exists) configWritten = fi0.LastWriteTimeUtc;
            }
            catch (Exception ex) { why = "读取异常 " + ex.GetType().Name + ": " + ex.Message; }
            if (trayOverride >= 0) { wantTray = trayOverride == 1; why = "命令行显式指定"; }
            // 必须 AutoReset：WaitOne(0) 取走信号就自动复位。用 ManualReset 的话信号永久有效，
            // 每个心跳都重弹一次菜单 —— 就是刚才"菜单关不掉、跟着鼠标"的直接原因。
            try { exitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, AppTitle + ".Exit"); }
            catch (Exception ex) { Log("退出信标创建失败 " + ex.Message); }
            try { previewHandle = new EventWaitHandle(false, EventResetMode.AutoReset, AppTitle + ".Preview"); }
            catch (Exception ex) { Log("预览信标创建失败 " + ex.Message); }

            Log("准备启动常驻，配置=" + ConfigPath() + " → 托盘=" + (wantTray ? "显示" : "隐藏") + "，依据：" + why);
            InstallHook();          // 先装钩子：即使托盘构建出问题，核心功能还在
            BuildDeskMenu();
            if (wantTray) BuildTray(); else Log("按配置不创建托盘图标");
            sync = new System.Windows.Forms.Timer();
            sync.Interval = 400;
            sync.Tick += delegate
            {
                RefreshState(true);
                if (exitHandle != null && exitHandle.WaitOne(0))
                {
                    Log("收到退出请求，结束常驻");
                    if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                    Application.Exit();
                    return;
                }
                if (previewHandle != null && previewHandle.WaitOne(0))
                {
                    Native.POINT cp = new Native.POINT();
                    Native.GetCursorPos(out cp);
                    Dictionary<string, string> cw = ReadConfig();
                    cw["tray"] = "1";
                    WriteConfig(cw);
                    EnsureTray(true);
                    if (menu != null)
                    {
                        try { menu.Show(new Point(cp.X, cp.Y)); Log("预览：已在光标 " + cp.X + "," + cp.Y + " 弹出托盘菜单"); }
                        catch (Exception ex) { Log("预览弹菜单失败 " + ex.Message); }
                    }
                }
                try
                {
                    // 配置文件被命令行改过（比如 DesktopShade.exe --tray），400ms 内跟上，不用重启
                    FileInfo fi = new FileInfo(ConfigPath());
                    if (fi.Exists && fi.LastWriteTimeUtc != configWritten)
                    {
                        configWritten = fi.LastWriteTimeUtc;
                        Dictionary<string, string> cfg = ReadConfig();
                        EnsureTray(!cfg.ContainsKey("tray") || cfg["tray"] != "0");
                        ApplyAutostart(cfg);      // 手改 ini 里的 autostart 也立即生效
                    }
                }
                catch (Exception ex) { Log("检查配置出错 " + ex.GetType().Name + ": " + ex.Message); }
            };
            Application.ApplicationExit += RestoreOnExit;         // 菜单/托盘里的退出
            AppDomain.CurrentDomain.ProcessExit += RestoreOnExit;  // 兜底：注销、ExitThread 等
            sync.Start();
            Application.Run();
        }
        catch (Exception ex)
        {
            Log("致命错误 " + ex.GetType().Name + ": " + ex.Message + " | " + ex.StackTrace);
            try
            {
                MessageBox.Show("桌面图标助手启动失败：" + ex.Message + "\n\n日志：" + LogPath(),
                    "DesktopShade", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            return 3;
        }
        finally { Cleanup(); }
        return 0;
    }

    // ---------- 定位 ----------
    static bool Locate()
    {
        // 光判"非零"不够：explorer 重启后旧句柄值还在但已作废，IsWindowVisible 对死句柄返回 false，
        // 会让常驻进程以为"图标已隐藏"从而静默失灵（本次实测踩到：kill explorer 之后缓存句柄全废）。
        if (progman != IntPtr.Zero && defView != IntPtr.Zero && listView != IntPtr.Zero
            && Native.IsWindow(progman) && Native.IsWindow(defView) && Native.IsWindow(listView)) return true;
        progman = IntPtr.Zero; defView = IntPtr.Zero; listView = IntPtr.Zero;
        List<IntPtr> found = new List<IntPtr>();
        Native.EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            StringBuilder sb = new StringBuilder(256);
            if (Native.GetClassName(h, sb, 256) > 0 && sb.ToString() == "Progman") found.Add(h);
            return true;
        }, IntPtr.Zero);
        if (found.Count == 0) return false;
        progman = found[0];
        defView = FindDescendant(progman, "SHELLDLL_DefView");
        if (defView == IntPtr.Zero) return false;
        listView = FindChild(defView, "SysListView32");
        if (listView == IntPtr.Zero) listView = FindDescendant(progman, "SysListView32");
        return listView != IntPtr.Zero;
    }

    static IntPtr FindChild(IntPtr parent, string cls)
    {
        List<IntPtr> acc = new List<IntPtr>();
        Native.EnumChildWindows(parent, delegate (IntPtr h, IntPtr l)
        {
            StringBuilder sb = new StringBuilder(256);
            if (Native.GetClassName(h, sb, 256) > 0 && sb.ToString() == cls) acc.Add(h);
            return true;
        }, IntPtr.Zero);
        // EnumChildWindows 会返回全部后代，这里优先取直系子级
        foreach (IntPtr h in acc) if (GetDirectParent(h) == parent) return h;
        return acc.Count > 0 ? acc[0] : IntPtr.Zero;
    }

    static IntPtr FindDescendant(IntPtr root, string cls)
    {
        List<IntPtr> acc = new List<IntPtr>();
        Native.EnumChildWindows(root, delegate (IntPtr h, IntPtr l)
        {
            StringBuilder sb = new StringBuilder(256);
            if (Native.GetClassName(h, sb, 256) > 0 && sb.ToString() == cls) acc.Add(h);
            return true;
        }, IntPtr.Zero);
        return acc.Count > 0 ? acc[0] : IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    static IntPtr GetDirectParent(IntPtr h) { return GetParent(h); }

    // ---------- 显隐 ----------
    static void SetHidden(bool hidden)
    {
        if (!Locate()) return;
        Native.ShowWindow(listView, hidden ? Native.SW_HIDE : Native.SW_SHOWNORMAL);
        Thread.Sleep(20);
        lastToggleMs = Environment.TickCount;
        Log((tray == null ? "CLI " : "") + (hidden ? "隐藏图标层" : "恢复图标层") + " listView=" + listView);
        RefreshState(false);
        if (tray != null) SetHiddenTrayState(hidden);
    }

    static void RefreshState(bool onlyIfChanged)
    {
        if (!Locate()) return;
        bool vis = Native.IsWindowVisible(listView);
        if (!onlyIfChanged || vis != iconsVisible)
        {
            iconsVisible = vis;
            if (tray != null) SetHiddenTrayState(!vis);
        }
    }

    // 外部（比如用户走 Windows 自己的"查看→显示桌面图标"）改了状态时，只同步界面不改窗口
    static void SetHiddenTrayState(bool hidden)
    {
        tray.Icon = MakeIcon(hidden);
        tray.Text = "桌面图标：" + (hidden ? "已隐藏（双击桌面空白处恢复）" : "显示中（双击桌面空白处隐藏）");
        miHide.Enabled = !hidden; miShow.Enabled = hidden;
        miHide.Checked = hidden; miShow.Checked = !hidden;
        if (miStatus != null)
        {
            string prefix = (trayLabels != null && trayLabels.Length > 0) ? trayLabels[0] : "当前";
            miStatus.Text = prefix + "：" + (hidden ? "已隐藏" : "显示中");
        }
    }

    // ---------- 鼠标钩子 ----------
    static void InstallHook()
    {
        hookProc = new Native.HookProc(OnMouse);
        IntPtr mod = Native.GetModuleHandle(null);
        hookId = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, hookProc, mod, 0);
        int err = hookId == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        Log("启动完成 pid=" + Process.GetCurrentProcess().Id + " hook=" + hookId + " win32err=" + err +
            " mod=" + mod + " progman=" + progman + " defView=" + defView + " listView=" + listView +
            " iconsVisible=" + Native.IsWindowVisible(listView));
        if (hookId == IntPtr.Zero)
        {
            BALLOON("DesktopShade", "鼠标钩子安装失败(错误码 " + err + ")，双击切换不可用；托盘菜单仍可手动切换。");
        }
    }

    static int lastDownX = 0, lastDownY = 0;
    static uint lastDownTime = 0;
    static bool haveLastDown = false;
    static bool firstDownLogged = false;
    static Native.POINT pendingMenuPoint;
    static System.Windows.Forms.Timer menuTimer;

    static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int m = (int)wParam;
                // 任何一次按下都先用来判断"是不是点在菜单外面"，是就收起（不吞掉这次点击）
                if (m == Native.WM_LBUTTONDOWN || m == Native.WM_RBUTTONDOWN || m == Native.WM_MBUTTONDOWN)
                {
                    Native.MSLLHOOKSTRUCT dm = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    DismissDeskMenuIfOutside(dm.pt);
                }
                if (m == Native.WM_LBUTTONDOWN)
                {
                    // WH_MOUSE_LL 只给物理按下，双击要自己配对：
                    // 与上一次按下间隔 <= 系统双击间隔、位移在容差框内，就算一次双击。
                    Native.MSLLHOOKSTRUCT d = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (!firstDownLogged)
                    {
                        firstDownLogged = true;
                        Log("钩子已收到第一个左键按下 @" + d.pt.X + "," + d.pt.Y);
                    }
                    int tol = Math.Max(4, Native.GetSystemMetrics(Native.SM_CXDOUBLECLK));
                    bool isPair = haveLastDown
                                  && (d.time - lastDownTime) <= Native.GetDoubleClickTime()
                                  && Math.Abs(d.pt.X - lastDownX) <= tol
                                  && Math.Abs(d.pt.Y - lastDownY) <= tol;
                    haveLastDown = true;
                    lastDownX = d.pt.X; lastDownY = d.pt.Y; lastDownTime = d.time;
                    if (isPair)
                    {
                        haveLastDown = false;
                        HandlePossible(d.pt);
                    }
                }
                else if (m == Native.WM_LBUTTONDBLCLK)
                {
                    // 少数情况下系统仍会往低级钩子送 DBLCLK，靠 300ms 去重避免来回抖
                    Native.MSLLHOOKSTRUCT d = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    HandlePossible(d.pt);
                }
                else if (m == Native.WM_RBUTTONDOWN)
                {
                    haveLastDown = false;      // 换别的键了，双击序列作废
                    Native.MSLLHOOKSTRUCT d = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (ShouldTakeOverRight(d.pt))
                    {
                        if (bypassRightOnce)
                        {
                            bypassRightOnce = false;
                            Log("隐藏态右键放行 → 交给 Windows 原生菜单 @" + d.pt.X + "," + d.pt.Y);
                        }
                        else
                        {
                            swallowRightUp = true;
                            pendingMenuPoint = d.pt;
                            Log("隐藏态右键接管 @" + d.pt.X + "," + d.pt.Y);
                            return (IntPtr)1;          // 吞掉，Explorer 就不会弹 Win11 那层要两跳的菜单
                        }
                    }
                }
                else if (m == Native.WM_RBUTTONUP && swallowRightUp)
                {
                    swallowRightUp = false;
                    ShowDeskMenu(pendingMenuPoint);
                    return (IntPtr)1;
                }
                else if (m == Native.WM_MBUTTONDOWN)
                {
                    haveLastDown = false;
                }
            }
        }
        catch (Exception ex) { Log("钩子回调异常 " + ex.GetType().Name + ": " + ex.Message); }
        return Native.CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    static void HandlePossible(Native.POINT pt)
    {
        dblClkSeen++;
        if (!Locate()) { Log("#" + dblClkSeen + " 定位不到桌面窗口，忽略"); return; }
        // 300ms 内不重复响应，避免一次操作被拆成多条 DBLCLK 导致来回抖
        int t = Environment.TickCount;
        if (Math.Abs(t - lastToggleMs) < 300)
        {
            Log("#" + dblClkSeen + " 距上次切换不足 300ms，忽略 @" + pt.X + "," + pt.Y);
            return;
        }

        IntPtr hit = Native.WindowFromPoint(pt);
        IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (hit == IntPtr.Zero || root != progman)
        {
            Log("#" + dblClkSeen + " 不在桌面（命中 " + ClassOf(hit) + " 根 " + ClassOf(root) + "）@" + pt.X + "," + pt.Y);
            return;
        }

        bool visible = Native.IsWindowVisible(listView);
        if (visible)
        {
            int item = IconItemAt(pt);
            if (item < 0)
            {
                Log("#" + dblClkSeen + " 桌面空白处双击 → 隐藏 @" + pt.X + "," + pt.Y);
                SetHidden(true);
            }
            else
            {
                Log("#" + dblClkSeen + " 点上有图标 item=" + item + "，不动作 @" + pt.X + "," + pt.Y);
            }
        }
        else
        {
            Log("#" + dblClkSeen + " 已隐藏，桌面双击 → 恢复 @" + pt.X + "," + pt.Y);
            SetHidden(false);
        }
    }

    // 跨进程命中测试：把 LVHITTESTINFO 写进 explorer 的内存里再发消息
    static int IconItemAt(Native.POINT screenPt)
    {
        uint pid;
        Native.GetWindowThreadProcessId(listView, out pid);
        Process explorer = null;
        try { explorer = Process.GetProcessById((int)pid); } catch { return -1; }
        IntPtr mem = IntPtr.Zero;
        try
        {
            const int SIZE = 32;     // 结构实际 20 字节，留余量给后续字段，别让 Explorer 写越界
            mem = Native.VirtualAllocEx(explorer.Handle, IntPtr.Zero, SIZE,
                Native.alloc_type.Commit | Native.alloc_type.Reserve, Native.page_prot.ReadWrite);
            if (mem == IntPtr.Zero) return -1;
            Native.POINT cl = screenPt;
            Native.ScreenToClient(listView, ref cl);
            byte[] buf = new byte[SIZE];
            WriteI32(buf, 0, cl.X); WriteI32(buf, 4, cl.Y);
            WriteI32(buf, 8, 0); WriteI32(buf, 12, 0); WriteI32(buf, 16, 0);
            int written;
            if (!Native.WriteProcessMemory(explorer.Handle, mem, buf, SIZE, out written)) return -1;
            IntPtr res;
            Native.SendMessageTimeout(listView, Native.LVM_HITTEST, IntPtr.Zero, mem,
                Native.SMTO_ABORTIFHUNG, 800, out res);
            byte[] outBuf = new byte[SIZE];
            int read;
            if (!Native.ReadProcessMemory(explorer.Handle, mem, outBuf, SIZE, out read)) return -1;
            uint flags = (uint)ReadI32(outBuf, 8);     // LVHT_* 标志
            int item = ReadI32(outBuf, 12);            // 命中的条目序号，-1 = 没命中
            if (item < 0) return -1;
            if ((flags & Native.LVHT_ONITEM) == 0) return -1;
            return item;
        }
        catch { return -1; }
        finally
        {
            if (mem != IntPtr.Zero) Native.VirtualFreeEx(explorer.Handle, mem, 0, Native.free_type.Release);
            if (explorer != null) explorer.Dispose();
        }
    }

    static void WriteI32(byte[] b, int at, int v) { byte[] x = BitConverter.GetBytes(v); x.CopyTo(b, at); }
    static int ReadI32(byte[] b, int at) { return BitConverter.ToInt32(b, at); }

    // ---------- 隐藏态右键：自己的一层菜单 ----------
    static bool ShouldTakeOverRight(Native.POINT pt)
    {
        if (!Locate()) return false;
        if (Native.IsWindowVisible(listView)) return false;   // 图标还开着，右键完全按 Windows 原样走
        IntPtr hit = Native.WindowFromPoint(pt);
        return hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GA_ROOT) == progman;
    }

    static void ShowDeskMenu(Native.POINT pt)
    {
        pendingMenuPoint = pt;
        menuTimer.Start();     // 回到消息循环里再弹，别在钩子回调内部 Show
    }

    // 自绘菜单（桌面那层和托盘那层）都没有 owner 窗口，失活时不会自己关；
    // 用同一个鼠标钩子补上：点到菜单外就收起。
    static void DismissDeskMenuIfOutside(Native.POINT pt)
    {
        CloseIfOutside(deskMenu, pt, "桌面菜单");
        CloseIfOutside(menu, pt, "托盘菜单");
    }

    static void CloseIfOutside(ContextMenuStrip which, Native.POINT pt, string label)
    {
        try
        {
            if (which == null || !which.Visible) return;
            if (which.Bounds.Contains(pt.X, pt.Y)) return;         // 点在菜单里，交给菜单自己处理
            which.Close();
            Log("点在" + label + "外 (" + pt.X + "," + pt.Y + ")，已收起");
        }
        catch (Exception ex) { Log("收起" + label + "出错 " + ex.GetType().Name + ": " + ex.Message); }
    }

    static void BuildDeskMenu()
    {
        deskMenu = new ContextMenuStrip();
        deskMenu.ShowImageMargin = false;
        ToolStripMenuItem itShow = new ToolStripMenuItem("显示桌面图标");
        itShow.Click += delegate { SetHidden(false); };
        ToolStripMenuItem itNative = new ToolStripMenuItem("Windows 原生菜单（新建 / 刷新 / 个性化）");
        itNative.Click += delegate
        {
            bypassRightOnce = true;   // 本次放行，让 Explorer 自己弹
            Native.mouse_event(Native.MOUSEEVENTF_RBUTTONDOWN, 0, 0, 0, IntPtr.Zero);
            Native.mouse_event(Native.MOUSEEVENTF_RBUTTONUP, 0, 0, 0, IntPtr.Zero);
        };
        // 托盘可以关掉，所以控制入口也得放进这层右键菜单里
        // 托盘那条：没自定义名字时按当前状态自动写「显示/隐藏托盘图标」
        itTrayToggle = new ToolStripMenuItem();
        itTrayToggle.Click += delegate { SetTrayWanted(tray == null); };
        ToolStripMenuItem itQuit = new ToolStripMenuItem("退出 DesktopShade（下次登录仍会自启）");
        itQuit.Click += delegate
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            Application.Exit();
        };
        ToolStripMenuItem hint = new ToolStripMenuItem("提示：双击桌面空白处也能恢复");
        hint.Enabled = false;
        deskMenu.Opening += delegate
        {
            // 每次弹出前按配置重刷四行文字，改 ini 立即见效，不用重启
            string[] lab = DeskMenuLabels(ReadConfig(), tray != null);
            itShow.Text = lab[0]; itNative.Text = lab[1]; itTrayToggle.Text = lab[2]; itQuit.Text = lab[3];
            // 回读控件文字留痕：这就是屏幕上会被画出来的东西
            Log("桌面菜单实际文字=" + itShow.Text + " ／ " + itNative.Text + " ／ " + itTrayToggle.Text + " ／ " + itQuit.Text);
        };
        deskMenu.Items.AddRange(new ToolStripItem[] { itShow, itNative, itTrayToggle, itQuit,
            new ToolStripSeparator(), hint });

        menuTimer = new System.Windows.Forms.Timer();
        menuTimer.Interval = 1;
        menuTimer.Tick += delegate
        {
            menuTimer.Stop();
            try { deskMenu.Show(new Point(pendingMenuPoint.X, pendingMenuPoint.Y)); }
            catch (Exception ex) { Log("弹菜单失败 " + ex.GetType().Name + ": " + ex.Message); }
        };
    }

    // ---------- 托盘 ----------
    // 这次弹出是不是从托盘图标来的（菜单落点要不要由我们纠正）
    static bool trayShownFlag = false;
    static Native.POINT trayCursorPos;

    static bool RootIsTray(Native.POINT pt)
    {
        IntPtr hit = Native.WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return false;
        IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root == IntPtr.Zero) return false;
        string c = ClassOf(root);
        return c == "Shell_TrayWnd" || c == "NotifyIconOverflowWindow" || c == "WinSysTrayOverflowWindow"
               || c == "TopLevelWindowForOverflowXamlIsland";
    }

    static void BuildTray()
    {
        menu = new ContextMenuStrip();
        miStatus = new ToolStripMenuItem("当前：显示中");
        miStatus.Enabled = false;
        miHide = new ToolStripMenuItem("隐藏桌面图标");
        miShow = new ToolStripMenuItem("显示桌面图标");
        miHide.Click += delegate { SetHidden(true); };
        miShow.Click += delegate { SetHidden(false); };
        ToolStripMenuItem miAgain = new ToolStripMenuItem("再点一次空白处也能恢复");
        miAgain.Enabled = false;
        miAuto = new ToolStripMenuItem("登录时自动启动（等于改 ini 里的 autostart）");
        miAuto.Checked = AutoStartOn();
        miAuto.Click += delegate
        {
            bool want = !miAuto.Checked;
            Dictionary<string, string> c = ReadConfig();
            c["autostart"] = want ? "True" : "False";
            WriteConfig(c);
            ApplyAutostart(c);
            miAuto.Checked = AutoStartOn();
            if (want && !miAuto.Checked) BALLOON("DesktopShade", "自启项设置失败（注册表写入被拒？）");
        };
        ToolStripMenuItem miTrayHide = new ToolStripMenuItem("隐藏这个托盘图标");
        miTrayHide.Click += delegate { SetTrayWanted(false); };
        ToolStripMenuItem miQuit = new ToolStripMenuItem("退出 DesktopShade");
        miQuit.Click += delegate { if (tray != null) tray.Visible = false; Application.Exit(); };
        menu.Opening += delegate
        {
            // 记下"是不是在托盘上右击的"和当时的光标位置：弹完要按它纠正落点
            Native.GetCursorPos(out trayCursorPos);
            trayShownFlag = RootIsTray(trayCursorPos);
            // 每次展开前按配置重刷文字，改 ini 立即见效
            string[] l = TrayMenuLabels(ReadConfig());
            trayLabels = l;
            miStatus.Text = l[0] + "：" + (!Native.IsWindowVisible(listView) ? "已隐藏" : "显示中");
            miHide.Text = l[1]; miShow.Text = l[2]; miAgain.Text = l[3];
            miAuto.Text = l[4]; miTrayHide.Text = l[5]; miQuit.Text = l[6];
            Log("托盘菜单实际文字=" + miStatus.Text + " ／ " + miHide.Text + " ／ " + miShow.Text + " ／ " + miAgain.Text
                + " ／ " + miAuto.Text + " ／ " + miTrayHide.Text + " ／ " + miQuit.Text);
        };
        menu.Opened += delegate    // ToolStripDropDown 弹出来之后触发的事件（Open 是方法，事件叫 Opened）
        {
            if (!trayShownFlag) return;
            trayShownFlag = false;
            try
            {
                Rectangle wa = Screen.FromPoint(new Point(trayCursorPos.X, trayCursorPos.Y)).WorkingArea;
                int wantX = Math.Min(trayCursorPos.X, wa.Right - menu.Width);
                int wantY = trayCursorPos.Y - menu.Height;         // 托盘在屏幕底边，菜单应该往上翻
                if (wantY < wa.Top) wantY = Math.Min(trayCursorPos.Y + 26, wa.Bottom - menu.Height);
                int dx = menu.Left - wantX, dy = menu.Top - wantY;
                Log("托盘菜单落点：实到 " + menu.Left + "," + menu.Top + "，应在 " + wantX + "," + wantY +
                    "，偏差 " + dx + "," + dy +
                    ((Math.Abs(dx) > 6 || Math.Abs(dy) > 6) ? "（已纠正）" : "（本来就没偏）"));
                if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6) menu.SetBounds(wantX, wantY, menu.Width, menu.Height);
            }
            catch (Exception ex) { Log("纠正托盘菜单落点失败 " + ex.GetType().Name + ": " + ex.Message); }
        };
        menu.Items.AddRange(new ToolStripItem[] { miStatus, new ToolStripSeparator(), miHide, miShow,
            miAgain, new ToolStripSeparator(), miAuto, miTrayHide, miQuit });
        var names = new List<string>();
        foreach (ToolStripItem it in menu.Items) if (it.Text.Length > 0) names.Add(it.Text);
        Log("托盘菜单项=" + string.Join(" | ", names.ToArray()));
        try { menu.CreateControl(); }      // 先把句柄建出来：托盘在屏幕角上，菜单要先知道自己多大才会往上/左翻位
        catch (Exception ex) { Log("预建托盘菜单句柄失败 " + ex.Message); }

        tray = new NotifyIcon();
        tray.ContextMenuStrip = menu;      // 交回给 NotifyIcon 自动弹：实测手弹+FlushMenu 会让菜单收不到点击
        tray.Icon = MakeIcon(false);
        tray.Visible = true;
        tray.DoubleClick += delegate { SetHidden(Native.IsWindowVisible(listView)); };
        RefreshState(false);
    }

    // 现画一个 16x16 图标：不往任何目录里塞 .ico 文件
    static Icon MakeIcon(bool hidden)
    {
        Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using (Brush bg = new SolidBrush(Color.FromArgb(255, 40, 90, 160)))
                g.FillRectangle(bg, 0, 0, 16, 16);
            if (!hidden)
            {
                using (Brush b = new SolidBrush(Color.White))
                {
                    g.FillRectangle(b, 2, 2, 4, 4);
                    g.FillRectangle(b, 9, 2, 4, 4);
                    g.FillRectangle(b, 2, 9, 4, 4);
                }
            }
            using (Pen p = new Pen(Color.FromArgb(255, 255, 200, 0), 2))
                g.DrawLine(p, hidden ? 3 : 10, 13, hidden ? 13 : 15, 13);
        }
        IntPtr hicon = bmp.GetHicon();
        Icon ico = Icon.FromHandle(hicon);
        Icon clone = (Icon)ico.Clone();
        ico.Dispose(); bmp.Dispose();
        DestroyIcon(hicon);
        return clone;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static void BALLOON(string title, string text)
    {
        if (tray == null) { Console.WriteLine(title + "：" + text); return; }
        try { tray.BalloonTipTitle = title; tray.BalloonTipText = text; tray.ShowBalloonTip(4000); } catch { }
    }

    // ---------- 自启 ----------
    static bool AutoStartOn()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
        {
            if (k == null) return false;
            object v = k.GetValue(RunValueName);
            return v != null && v.ToString().IndexOf("DesktopShade.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    static string AutoStartValue()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (k == null) return "";
                object v = k.GetValue(RunValueName);
                return v == null ? "" : v.ToString();
            }
        }
        catch { return ""; }
    }

    // 自启项指向的是不是当前这个 exe（换目录/覆盖安装时要比路径）
    static bool AutoStartPointsHere()
    {
        string v = AutoStartValue();
        if (v.Length == 0) return false;
        try { return v.IndexOf(Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) >= 0; }
        catch { return false; }
    }

    static bool SetAutoStart(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (k == null) return false;
                if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --quiet");
                else if (k.GetValue(RunValueName) != null) k.DeleteValue(RunValueName, false);
                return true;
            }
        }
        catch { return false; }
    }

    static string ClassOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        StringBuilder sb = new StringBuilder(256);
        return Native.GetClassName(h, sb, 256) > 0 ? sb.ToString() : "?";
    }

    // 诊断口：报告在这个屏幕点上，程序会做什么以及为什么
    static void Probe(int x, int y)
    {
        Native.POINT p = new Native.POINT();
        p.X = x; p.Y = y;
        IntPtr hit = Native.WindowFromPoint(p);
        IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
        bool onDesktop = (root == progman) && progman != IntPtr.Zero;
        bool visible = Native.IsWindowVisible(listView);
        int item = visible ? IconItemAt(p) : -9;
        string decision;
        if (!onDesktop) decision = "IGNORE_NOT_DESKTOP";
        else if (visible) decision = (item < 0 ? "WOULD_HIDE" : "IGNORE_ICON_UNDER_POINT");
        else decision = "WOULD_SHOW";
        Console.WriteLine("PROBE_POINT=" + x + "," + y);
        Console.WriteLine("HIT_CLASS=" + ClassOf(hit));
        Console.WriteLine("HIT_ROOT=" + ClassOf(root));
        Console.WriteLine("ON_DESKTOP=" + onDesktop);
        Console.WriteLine("ICONS_VISIBLE=" + visible);
        Console.WriteLine("ICON_ITEM=" + item + "   (-1=no-icon, -9=icons-hidden)");
        Console.WriteLine("DECISION=" + decision);
    }

    // 把每个桌面图标的屏幕位置打出来，给"这里到底算不算空白"提供对照
    static void Icons()
    {
        uint pid;
        Native.GetWindowThreadProcessId(listView, out pid);
        Process ex = null;
        try { ex = Process.GetProcessById((int)pid); }
        catch { Console.WriteLine("CANNOT_OPEN_EXPLORER"); return; }
        try
        {
            IntPtr res;
            IntPtr ok = Native.SendMessageTimeout(listView, Native.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 800, out res);
            long raw = res.ToInt64() & 0xFFFFFFFFL;   // 64 位下 ToInt32 对 0xFFFFFFFF 会抛 OverflowException
            int count = (ok != IntPtr.Zero && raw >= 0 && raw < 4096) ? (int)raw : 0;
            Console.WriteLine("ICON_COUNT=" + count + " RAW=0x" + raw.ToString("X") + " CALL_OK=" + (ok != IntPtr.Zero));
            if (raw == 0xFFFFFFFFL)
            {
                // 实测：桌面那个 SysListView32 对 LVM_GETITEMCOUNT 返回 -1、对 LVM_GETITEMPOSITION 不写回，
                // 它不是普通 listview。LVM_HITTEST 是有效的，所以判空白/图标可信，但条目枚举不可用。
                Console.WriteLine("NOTE=桌面视图不支持条目枚举，位置请改用网格 --probe 扫描（见 verify6.ps1）");
                return;
            }
            int scanned = count;
            if (count == 0) scanned = 400;   // 计数不可靠就逐个试探，靠 CALL_FAIL 收尾
            IntPtr mem = Native.VirtualAllocEx(ex.Handle, IntPtr.Zero, 8,
                Native.alloc_type.Commit | Native.alloc_type.Reserve, Native.page_prot.ReadWrite);
            if (mem == IntPtr.Zero) { Console.WriteLine("ALLOC_FAIL"); return; }
            try
            {
                byte[] buf = new byte[8];
                int w;
                for (int i = 0; i < scanned; i++)
                {
                    WriteI32(buf, 0, 0); WriteI32(buf, 4, 0);
                    if (!Native.WriteProcessMemory(ex.Handle, mem, buf, 8, out w)) { Console.WriteLine("ITEM " + i + " WRITE_FAIL"); continue; }
                    IntPtr r2;
                    IntPtr ok2 = Native.SendMessageTimeout(listView, Native.LVM_GETITEMPOSITION, (IntPtr)i, mem,
                        Native.SMTO_ABORTIFHUNG, 800, out r2);
                    if (ok2 == IntPtr.Zero) { Console.WriteLine("ITEM " + i + " CALL_FAIL"); break; }
                    byte[] o = new byte[8];
                    int rd;
                    if (!Native.ReadProcessMemory(ex.Handle, mem, o, 8, out rd)) { Console.WriteLine("ITEM " + i + " READ_FAIL"); continue; }
                    Native.POINT p = new Native.POINT();
                    p.X = ReadI32(o, 0); p.Y = ReadI32(o, 4);
                    Native.ClientToScreen(listView, ref p);
                    Console.WriteLine("ITEM " + i + " SCREEN=" + p.X + "," + p.Y);
                }
            }
            finally { Native.VirtualFreeEx(ex.Handle, mem, 0, Native.free_type.Release); }
        }
        finally { ex.Dispose(); }
    }

    static void PrintStatus()
    {
        Console.WriteLine("PROGMAN=" + progman);
        Console.WriteLine("DEFVIEW=" + defView);
        Console.WriteLine("LISTVIEW=" + listView);
        Console.WriteLine("ICONS_VISIBLE=" + Native.IsWindowVisible(listView));
        Console.WriteLine("DBLCLK_MS=" + Native.GetDoubleClickTime() + " TOLERANCE=" +
            Native.GetSystemMetrics(Native.SM_CXDOUBLECLK) + "x" + Native.GetSystemMetrics(Native.SM_CYDOUBLECLK));
        int others = 0;
        foreach (Process pp in Process.GetProcessesByName("DesktopShade"))
        {
            if (pp.Id != Process.GetCurrentProcess().Id) others++;
            pp.Dispose();
        }
        Console.WriteLine("RESIDENT_INSTANCES=" + others);
        Console.WriteLine("LOGFILE=" + LogPath());
        Console.WriteLine("CONFIG=" + ConfigPath());
        Dictionary<string, string> cs = ReadConfig();
        Console.WriteLine("TRAY=" + ((!cs.ContainsKey("tray") || cs["tray"] != "0") ? "on" : "off"));
        Console.WriteLine("AUTOSTART=" + (AutoStartOn() ? "on" : "off"));
        string av = AutoStartValue();
        Console.WriteLine("AUTOSTART_VALUE=" + (av.Length > 0 ? av : "(无)"));
        Console.WriteLine("AUTOSTART_POINTS_HERE=" + AutoStartPointsHere());
        Console.WriteLine("AUTOSTART_CFG=" + (cs.ContainsKey("autostart") ? cs["autostart"] : "(未设置)"));
        Console.WriteLine("LOGCLEAN_CFG=" + (cs.ContainsKey("logclean") ? cs["logclean"] : "(未设置→按 3days)"));
        string[] lab = DeskMenuLabels(cs, false);
        Console.WriteLine("MENU1=" + lab[0]);
        Console.WriteLine("MENU2=" + lab[1]);
        Console.WriteLine("MENU3=" + lab[2]);
        Console.WriteLine("MENU4=" + lab[3]);
        string[] tl = TrayMenuLabels(cs);
        for (int ti = 0; ti < tl.Length; ti++) Console.WriteLine("TRAYMENU" + (ti + 1) + "=" + tl[ti]);
    }

    static void Cleanup()
    {
        if (hookId != IntPtr.Zero) Native.UnhookWindowsHookEx(hookId);
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        if (exitHandle != null) { exitHandle.Close(); exitHandle = null; }
        if (previewHandle != null) { previewHandle.Close(); previewHandle = null; }
        if (singleInstance != null) singleInstance.ReleaseMutex();
    }
}
