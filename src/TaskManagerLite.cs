// Task Manager Lite - lightweight process and memory monitor for Windows, with a Smart Advisor,
// Ask AI (Claude Code), auto troubleshooting and self-update from this source folder.
// Build: build.bat (uses the C# compiler that ships with .NET Framework 4.8 on Windows).
//
// Low-overhead design (as in System Informer / Process Hacker):
//  - one NtQuerySystemInformation call reads every process (CPU times, memory, priority) without opening handles
//  - whole-system CPU from GetSystemTimes; memory + commit from one GetPerformanceInfo call
//  - per-process CPU = change in CPU time between refreshes (no sleeping, no performance counters)
//  - the list and advisor cards are only redrawn while visible
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Task Manager Lite")]
[assembly: System.Reflection.AssemblyDescription("Lightweight process and memory monitor with Smart Advisor and Ask AI")]
[assembly: System.Reflection.AssemblyCompany("Vk Professional Service")]
[assembly: System.Reflection.AssemblyProduct("Task Manager Lite")]
[assembly: System.Reflection.AssemblyVersion(TaskManagerLite.AppInfo.Version + ".0")]
[assembly: System.Reflection.AssemblyFileVersion(TaskManagerLite.AppInfo.Version + ".0")]

namespace TaskManagerLite
{
    static class AppInfo
    {
        // Raise this number when you change the code; the running app then offers (or installs) the update.
        public const string Version = "2.1.0";
        public const string Name = "Task Manager Lite";
    }

    // ------------------------------------------------------------------ Windows API
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint Low, High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                           SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        [DllImport("psapi.dll")] public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint size);
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string idList);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        // ---- memory cleanup
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("ntdll.dll")] public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] public struct SHQUERYRBINFO { public int cbSize; public long Size; public long Items; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        public class MemLists { public long Free, Zero, Modified, Standby; }

        // SystemMemoryListInformation (class 80): readable without admin rights.
        public static MemLists QueryMemoryLists()
        {
            IntPtr b = Marshal.AllocHGlobal(256);
            try
            {
                int r;
                if (NtQuerySystemInformation(80, b, 256, out r) != 0) return null;
                long page = Environment.SystemPageSize;
                var m = new MemLists { Zero = Marshal.ReadInt64(b, 0) * page, Free = Marshal.ReadInt64(b, 8) * page, Modified = Marshal.ReadInt64(b, 16) * page };
                for (int i = 0; i < 8; i++) m.Standby += Marshal.ReadInt64(b, 40 + i * 8) * page;
                return m;
            }
            finally { Marshal.FreeHGlobal(b); }
        }

        public static bool EnablePrivilege(string name)
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out tok)) return false;   // ADJUST_PRIVILEGES | QUERY
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };   // SE_PRIVILEGE_ENABLED
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(tok); }
        }

        // Empties the standby list (Task Manager's "Cached"). Needs administrator rights.
        public static int PurgeStandby()
        {
            if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) return -1;
            int cmd = 4;   // MemoryPurgeStandbyList
            return NtSetSystemInformation(80, ref cmd, 4);
        }

        public static bool IsElevated()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
            }
            finally { CloseHandle(h); }
        }

        public static HashSet<int> HungWindowPids()
        {
            var set = new HashSet<int>();
            EnumWindows((h, l) =>
            {
                if (IsWindowVisible(h) && IsHungAppWindow(h))
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    set.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
            return set;
        }

        public static int ForegroundPid()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return (int)pid;
        }

        public static void SetCue(TextBox tb, string text) { SendMessage(tb.Handle, 0x1501, (IntPtr)1, text); }

        public static void DarkTitleBar(IntPtr h, bool dark)
        {
            int v = dark ? 1 : 0;
            try { if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4); } catch { }
        }
    }

    // ------------------------------------------------------------------ Data
    class Row
    {
        public string Name, Key, Priority = "–";
        public int Pid, Count = 1;
        public double Cpu;
        public bool CpuKnown;
        public long WorkingSet, Private;
        public List<int> Pids = new List<int>();
    }

    class SysInfo
    {
        public double Cpu = -1;
        public double MemTotal, MemAvail, CommitTotal, CommitLimit, Cache;   // bytes
        public int Processes, Threads, Handles;
        public double MemPct { get { return MemTotal > 0 ? (MemTotal - MemAvail) * 100 / MemTotal : 0; } }
        public double CommitPct { get { return CommitLimit > 0 ? CommitTotal * 100 / CommitLimit : 0; } }
        public double PageFile { get { return Math.Max(0, CommitLimit - MemTotal); } }
    }

    class Sampler
    {
        struct Prev { public ulong Created, Cpu; }

        Dictionary<int, Prev> prev = new Dictionary<int, Prev>();
        long prevStamp;
        ulong sysIdle, sysTotal;
        bool haveSys;
        IntPtr buf = IntPtr.Zero;
        int bufLen;
        readonly int cores = Environment.ProcessorCount;

        public SysInfo SampleSystem()
        {
            var s = new SysInfo();
            Native.FILETIME i, k, u;
            if (Native.GetSystemTimes(out i, out k, out u))
            {
                ulong idle = i.Value, total = k.Value + u.Value;   // kernel time includes idle time
                if (haveSys && total > sysTotal)
                {
                    double dt = total - sysTotal, di = idle - sysIdle;
                    s.Cpu = Math.Max(0, Math.Min(100, (dt - di) * 100 / dt));
                }
                sysIdle = idle; sysTotal = total; haveSys = true;
            }
            Native.PERFORMANCE_INFORMATION pi;
            if (Native.GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION))))
            {
                double page = pi.PageSize.ToUInt64();
                s.MemTotal = pi.PhysicalTotal.ToUInt64() * page;
                s.MemAvail = pi.PhysicalAvailable.ToUInt64() * page;
                s.CommitTotal = pi.CommitTotal.ToUInt64() * page;
                s.CommitLimit = pi.CommitLimit.ToUInt64() * page;
                s.Cache = pi.SystemCache.ToUInt64() * page;
                s.Processes = (int)pi.ProcessCount;
                s.Threads = (int)pi.ThreadCount;
                s.Handles = (int)pi.HandleCount;
            }
            return s;
        }

        // SystemProcessInformation (class 5). Offsets are for 64-bit Windows.
        public List<Row> SampleProcesses()
        {
            var rows = new List<Row>();
            if (!Query()) return rows;
            long now = Stopwatch.GetTimestamp();
            double elapsed = prevStamp == 0 ? 0 : (now - prevStamp) * 1e7 / Stopwatch.Frequency;   // 100-ns units
            var next = new Dictionary<int, Prev>();
            int off = 0;
            while (true)
            {
                IntPtr e = IntPtr.Add(buf, off);
                int pid = (int)Marshal.ReadInt64(e, 80);
                if (pid != 0)   // skip "System Idle Process"
                {
                    int len = (ushort)Marshal.ReadInt16(e, 56);
                    IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                    string name = len > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, len / 2) : "System";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    var r = new Row { Pid = pid, Name = name, Key = "p" + pid };
                    r.Pids.Add(pid);
                    r.WorkingSet = Marshal.ReadInt64(e, 8);     // private working set (Windows Task Manager's "Memory")
                    r.Private = Marshal.ReadInt64(e, 200);      // private bytes = share of committed memory
                    r.Priority = PriorityName(Marshal.ReadInt32(e, 72));
                    var cur = new Prev { Created = (ulong)Marshal.ReadInt64(e, 32), Cpu = (ulong)(Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48)) };
                    Prev old;
                    if (elapsed > 0 && prev.TryGetValue(pid, out old) && old.Created == cur.Created && cur.Cpu >= old.Cpu)
                    {
                        r.Cpu = Math.Min(100, (cur.Cpu - old.Cpu) * 100.0 / (elapsed * cores));
                        r.CpuKnown = true;
                    }
                    next[pid] = cur;
                    rows.Add(r);
                }
                int nextOff = Marshal.ReadInt32(e, 0);
                if (nextOff == 0) break;
                off += nextOff;
            }
            prev = next;
            prevStamp = now;
            return rows;
        }

        bool Query()
        {
            if (buf == IntPtr.Zero) { bufLen = 512 * 1024; buf = Marshal.AllocHGlobal(bufLen); }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int needed;
                int status = Native.NtQuerySystemInformation(5, buf, bufLen, out needed);
                if (status == 0) return true;
                if ((uint)status != 0xC0000004) return false;   // anything except "buffer too small"
                Marshal.FreeHGlobal(buf);
                bufLen = Math.Max(needed, bufLen) + 64 * 1024;
                buf = Marshal.AllocHGlobal(bufLen);
            }
            return false;
        }

        static string PriorityName(int basePriority)
        {
            if (basePriority <= 0) return "–";
            if (basePriority <= 4) return "Low";
            if (basePriority <= 6) return "Below normal";
            if (basePriority <= 9) return "Normal";
            if (basePriority <= 12) return "Above normal";
            if (basePriority <= 15) return "High";
            return "Realtime";
        }
    }

    // ------------------------------------------------------------------ Built-in process knowledge (offline)
    static class Knowledge
    {
        public class Info { public string Desc, Safe; }
        static readonly Dictionary<string, Info> map = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);

        static void A(string names, string safe, string desc)
        {
            foreach (var n in names.Split('|')) map[n] = new Info { Desc = desc, Safe = safe };
        }

        static Knowledge()
        {
            A("chrome", "Careful – closes tabs (including Quantman)", "Google Chrome. Every tab, extension and helper runs as its own process. Your Quantman trading tab runs here.");
            A("msedge", "Careful – closes its tabs", "Microsoft Edge browser.");
            A("msedgewebview2", "Careful – may break the app that uses it", "Edge WebView2: a built-in browser engine used by desktop apps such as Claude, ChatGPT, Widgets and Teams.");
            A("Code", "Careful – save your files first", "Visual Studio Code: editor windows, extensions and language servers.");
            A("node", "Careful – may stop a tool or dev server", "Node.js: usually VS Code extensions, Claude Code helpers or development servers.");
            A("claude", "Careful – stops a running Claude task", "Claude: the Claude desktop app or Claude Code (check the location below).");
            A("ChatGPT", "Yes – chats are saved online", "ChatGPT desktop app. Using ChatGPT in a Chrome tab instead usually saves about 1 GB of memory.");
            A("codex", "Careful – stops a running task", "OpenAI Codex command-line agent.");
            A("python|pythonw|py", "Careful – stops the script or agent", "Python: scripts and agents such as Hermes.");
            A("hermes", "Careful – stops the agent", "Hermes agent.");
            A("ollama|ollama_llama_server", "Yes – reloads the model next time", "Ollama: runs AI models locally. Loaded models use a lot of memory.");
            A("MsMpEng|NisSrv|SecurityHealthService|MpDefenderCoreService", "No – part of Windows security", "Microsoft Defender Antivirus. High CPU usually means it is scanning; it settles down on its own.");
            A("Memory Compression", "No – part of Windows", "Windows compresses memory here to fit more into RAM. A large value means RAM is under pressure.");
            A("dwm", "No – part of Windows", "Desktop Window Manager: draws everything on screen. High CPU usually points to graphics drivers or visual effects.");
            A("explorer", "Careful – taskbar disappears until restarted", "Windows Explorer: the taskbar, Start menu and file windows.");
            A("svchost", "No – part of Windows", "Service Host: runs Windows services.");
            A("System|Registry|smss|csrss|wininit|winlogon|services|lsass|lsaiso|fontdrvhost|sihost", "No – part of Windows", "Core Windows process.");
            A("SearchIndexer|SearchApp|SearchHost|SearchProtocolHost|SearchFilterHost", "Careful – search pauses", "Windows Search indexing.");
            A("WmiPrvSE", "Careful", "WMI Provider Host: answers system queries from apps. High CPU means some app is polling it often.");
            A("RuntimeBroker", "Careful", "Runtime Broker: manages permissions for Store apps.");
            A("OneDrive", "Yes – sync resumes when reopened", "OneDrive file sync.");
            A("Teams|ms-teams", "Yes", "Microsoft Teams.");
            A("powershell|pwsh|cmd|conhost|WindowsTerminal|OpenConsole", "Careful – may stop a running script", "Command-line shell or console. It may be running a script or a Claude Code command.");
            A("Taskmgr", "Yes", "Windows Task Manager.");
            A("TaskManagerLite", "Yes", "This app (Task Manager Lite).");
            A("OfficeClickToRun", "Careful", "Microsoft Office background updater and installer.");
            A("spoolsv", "Careful – printing stops", "Print Spooler.");
            A("audiodg", "Careful – sound stops briefly", "Windows audio engine.");
            A("ctfmon", "Careful – typing input may break", "Text input and keyboard languages.");
            A("AnyDesk|UltraViewer_Service|UltraViewer_Desktop|TeamViewer", "Careful – remote sessions disconnect", "Remote desktop software.");
            A("WhatsApp|Telegram", "Yes", "Messaging app.");
            A("PowerToys|PowerToys.Peek.UI|PowerToys.Runner", "Yes", "Microsoft PowerToys utilities.");
            A("git|git-remote-https", "Careful", "Git version control.");
        }

        public static Info Get(string name) { Info i; return map.TryGetValue(name, out i) ? i : null; }
    }

    // ------------------------------------------------------------------ Smart Advisor findings
    enum Sev { Good = 0, Info = 1, Warning = 2, Critical = 3 }

    class Finding
    {
        public string Id, Title, Detail, ActionText;
        public Sev Severity;
        public Action Act;
    }

    // ------------------------------------------------------------------ Theme
    static class T
    {
        public static bool Dark;
        public static Color Bg, Surface, Surface2, Input, Text, Sub, Border, Sel, Heat,
                            Sidebar, SideText, SideSub, SideHover, SideSel;
        public static readonly Color Accent = Color.FromArgb(0, 120, 215);
        public static readonly Color Red = Color.FromArgb(214, 59, 47);
        public static readonly Color Orange = Color.FromArgb(222, 131, 0);
        public static readonly Color Green = Color.FromArgb(46, 160, 67);
        public static readonly Color Blue = Color.FromArgb(0, 120, 215);

        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static void Set(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Bg = C(24, 24, 27); Surface = C(34, 34, 38); Surface2 = C(43, 43, 48); Input = C(30, 30, 34);
                Text = C(236, 236, 240); Sub = C(160, 160, 170); Border = C(60, 60, 67); Sel = C(36, 74, 116);
                Heat = C(150, 104, 18); Sidebar = C(17, 17, 20); SideHover = C(32, 32, 38); SideSel = C(42, 42, 50);
            }
            else
            {
                Bg = C(242, 243, 246); Surface = Color.White; Surface2 = C(247, 248, 250); Input = Color.White;
                Text = C(28, 28, 32); Sub = C(98, 100, 110); Border = C(222, 225, 230); Sel = C(204, 228, 255);
                Heat = C(255, 200, 80); Sidebar = C(25, 33, 49); SideHover = C(36, 46, 66); SideSel = C(46, 59, 84);
            }
            SideText = C(236, 239, 245); SideSub = C(148, 158, 176);
        }

        public static Color Blend(Color a, Color b, double f)
        {
            f = Math.Max(0, Math.Min(1, f));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
        }

        public static Color Of(Sev s)
        {
            switch (s)
            {
                case Sev.Critical: return Red;
                case Sev.Warning: return Orange;
                case Sev.Info: return Blue;
                default: return Green;
            }
        }
    }

    static class F
    {
        public static readonly Font Body = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 11f);
        public static readonly Font H1 = new Font("Segoe UI Semibold", 16f);
        public static readonly Font Big = new Font("Segoe UI Semibold", 28f);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font SmallBold = new Font("Segoe UI Semibold", 7.5f);
        public static readonly Font Icon = new Font("Segoe MDL2 Assets", 12f);
        public static readonly Font Mono = new Font("Consolas", 9f);
        public static readonly Font Side = new Font("Segoe UI Semibold", 12.5f);
        public static readonly Font TileTitle = new Font("Segoe UI", 9.5f);
        public static readonly Font TileValue = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Md = new Font("Segoe UI", 10f);
        public static readonly Font MdBold = new Font("Segoe UI Semibold", 10f);
        public static readonly Font MdHead = new Font("Segoe UI Semibold", 12.5f);
        public static readonly Font Input = new Font("Segoe UI", 10f);
    }

    static class UI
    {
        public static Button Btn(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30),
                Padding = new Padding(8, 0, 8, 0), FlatStyle = FlatStyle.Flat, Tag = primary ? "primary" : null,
                Margin = new Padding(0, 0, 8, 6), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        public static Label Lbl(string text, string tag)
        {
            return new Label { Text = text, AutoSize = true, Tag = tag, Margin = new Padding(0, 7, 8, 0) };
        }

        public static CheckBox Chk(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 6, 18, 0), Cursor = Cursors.Hand };
        }
    }

    // ------------------------------------------------------------------ Custom controls
    class Tile : Control
    {
        const int MaxPoints = 60;
        public string Title, Value = "…", Sub = "", Sub2 = "";
        public double Percent = -1;
        public bool Graph = true;
        public Color Accent;
        readonly List<double> history = new List<double>();

        public Tile(string title, Color accent)
        {
            Title = title; Accent = accent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(5);
            Dock = DockStyle.Fill;
        }

        public void Push(double v)
        {
            if (v < 0) return;
            history.Add(v);
            if (history.Count > MaxPoints) history.RemoveAt(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(T.Surface);
            using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 0, 4, h);
            Color valueColor = Percent >= 85 ? T.Red : T.Text;
            TextRenderer.DrawText(g, Title, F.TileTitle, new Point(14, 8), T.Sub);
            TextRenderer.DrawText(g, Value, F.TileValue, new Point(12, 26), valueColor);
            TextRenderer.DrawText(g, Sub, F.Small, new Point(14, 58), T.Sub);
            if (!Graph)
            {
                TextRenderer.DrawText(g, Sub2, F.Small, new Point(14, 76), T.Sub);
                return;
            }
            var r = new Rectangle(14, 78, w - 28, h - 88);
            if (r.Width < 10 || r.Height < 8) return;
            using (var bg = new SolidBrush(T.Surface2)) g.FillRectangle(bg, r);
            if (history.Count < 2) return;
            var pts = new PointF[history.Count];
            float step = r.Width / (float)(MaxPoints - 1);
            for (int i = 0; i < history.Count; i++)
            {
                float x = r.Right - (history.Count - 1 - i) * step;
                float y = r.Bottom - (float)(Math.Max(0, Math.Min(100, history[i])) / 100.0 * r.Height);
                pts[i] = new PointF(x, y);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var poly = new List<PointF>(pts);
            poly.Add(new PointF(pts[pts.Length - 1].X, r.Bottom));
            poly.Add(new PointF(pts[0].X, r.Bottom));
            using (var fill = new SolidBrush(Color.FromArgb(T.Dark ? 60 : 40, Accent))) g.FillPolygon(fill, poly.ToArray());
            using (var line = new Pen(Accent, 1.6f)) g.DrawLines(line, pts);
        }
    }

    class NavButton : Control
    {
        public string Glyph, Caption;
        public bool Selected;
        public int Badge;
        bool hover;

        public NavButton(string glyph, string caption)
        {
            Glyph = glyph; Caption = caption;
            Height = 44; Dock = DockStyle.Top; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Selected ? T.SideSel : hover ? T.SideHover : T.Sidebar);
            if (Selected) using (var b = new SolidBrush(T.Accent)) g.FillRectangle(b, 0, 9, 4, Height - 18);
            var c = Selected ? T.SideText : T.SideSub;
            TextRenderer.DrawText(g, Glyph, F.Icon, new Rectangle(14, 0, 30, Height), c, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Caption, Selected ? F.Bold : F.Body, new Rectangle(52, 0, Width - 90, Height), T.SideText, TextFormatFlags.VerticalCenter);
            if (Badge > 0)
            {
                string s = Badge > 9 ? "9+" : Badge.ToString();
                var r = new Rectangle(Width - 38, (Height - 20) / 2, 22, 20);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(T.Red)) g.FillEllipse(b, r);
                TextRenderer.DrawText(g, s, F.SmallBold, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    class Card : Panel
    {
        public readonly Finding Finding;
        readonly Label title, detail;
        readonly Button act, ask;

        public Card(Finding f, Action<Finding> onAct, Action<Finding> onAsk)
        {
            Finding = f;
            Tag = "surface";
            Margin = new Padding(0, 0, 0, 10);
            DoubleBuffered = true;
            ResizeRedraw = true;
            title = new Label { Text = f.Title, Font = F.H2, AutoSize = false, AutoEllipsis = true };
            detail = new Label { Text = f.Detail, AutoSize = false, Tag = "sub" };
            Controls.Add(title);
            Controls.Add(detail);
            if (f.Act != null && f.ActionText != null)
            {
                act = UI.Btn(f.ActionText, true);
                act.Click += delegate { onAct(f); };
                Controls.Add(act);
            }
            if (f.Severity != Sev.Good)
            {
                ask = UI.Btn("Ask AI about this", false);
                ask.Click += delegate { onAsk(f); };
                Controls.Add(ask);
            }
        }

        public void SetTexts(Finding f) { title.Text = f.Title; detail.Text = f.Detail; }

        public void LayoutFor(int width)
        {
            Width = width;
            int x = 22, w = Math.Max(100, width - x - 120);
            title.SetBounds(x, 12, w, 24);
            var sz = TextRenderer.MeasureText(detail.Text, detail.Font, new Size(width - x - 18, 2000), TextFormatFlags.WordBreak);
            detail.SetBounds(x, 40, width - x - 18, sz.Height + 4);
            int y = 40 + sz.Height + 10;
            int bx = x;
            if (act != null) { act.Location = new Point(bx, y); bx += act.PreferredSize.Width + 8; }
            if (ask != null) { ask.Location = new Point(bx, y); }
            if (act != null || ask != null) y += 38;
            Height = y + 4;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(T.Of(Finding.Severity))) g.FillRectangle(b, 0, 0, 5, Height);
            string chip = Finding.Severity == Sev.Critical ? "CRITICAL" : Finding.Severity == Sev.Warning ? "WARNING" : Finding.Severity == Sev.Info ? "TIP" : "OK";
            var sz = TextRenderer.MeasureText(chip, F.SmallBold);
            var r = new Rectangle(Width - sz.Width - 26, 14, sz.Width + 12, 20);
            using (var b = new SolidBrush(Color.FromArgb(T.Dark ? 70 : 35, T.Of(Finding.Severity)))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, chip, F.SmallBold, r, T.Of(Finding.Severity), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Memory composition bar: in use / modified / cached (standby) / free
    class MemBar : Control
    {
        public long InUse, Modified, Standby, Free, Total;

        public MemBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 76;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(T.Bg);
            var bar = new Rectangle(0, 0, Width - 1, 32);
            using (var b = new SolidBrush(T.Surface)) g.FillRectangle(b, bar);
            var parts = new[] {
                new KeyValuePair<string, long>("In use", InUse), new KeyValuePair<string, long>("Modified", Modified),
                new KeyValuePair<string, long>("Cached (standby)", Standby), new KeyValuePair<string, long>("Free", Free) };
            var colors = new[] { Color.FromArgb(139, 18, 174), T.Orange, Color.FromArgb(T.Dark ? 70 : 120, 0, 120, 215), T.Surface2 };
            double total = Math.Max(1, Total);
            float x = bar.X;
            for (int i = 0; i < parts.Length; i++)
            {
                float w = (float)(parts[i].Value / total * bar.Width);
                if (i == parts.Length - 1) w = bar.Right - x;
                if (w > 0) using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, x, bar.Y, w, bar.Height);
                x += w;
            }
            using (var p = new Pen(T.Border)) g.DrawRectangle(p, bar);
            int lx = 0, step = Width / 4;
            for (int i = 0; i < parts.Length; i++)
            {
                using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, lx, 46, 12, 12);
                using (var p = new Pen(T.Border)) g.DrawRectangle(p, lx, 46, 12, 12);
                TextRenderer.DrawText(g, string.Format("{0}  {1:0.0} GB", parts[i].Key, parts[i].Value / 1073741824.0), F.Body, new Point(lx + 18, 43), T.Text);
                lx += step;
            }
        }
    }

    // A junk-file location on disk
    class Junk
    {
        public string Name;
        public string[] Paths;
        public bool Admin, OlderThanDay, Recycle, DefaultOn = true;
        public long Bytes;
        public int Files;
        public CheckBox Box;
    }

    class DbListView : ListView
    {
        public DbListView() { DoubleBuffered = true; }
    }

    // ------------------------------------------------------------------ Main window
    class MainForm : Form
    {
        static readonly int[] IntervalSecs = { 10, 30, 60, 0 };
        static readonly string[] IntervalNames = { "10 seconds", "30 seconds", "60 seconds", "Paused" };
        static readonly string[] ColumnNames = { "Name", "PID", "CPU %", "Memory (MB)", "Commit (MB)", "Priority" };
        static readonly string[] Models = { "haiku", "sonnet", "opus" };
        static readonly string[] ModelNames = { "Fast (Haiku)", "Balanced (Sonnet)", "Best (Opus)" };
        // Core Windows processes: ending them or changing their priority can freeze or restart the PC.
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "sihost", "MsMpEng", "NisSrv",
            "SecurityHealthService", "MpDefenderCoreService" };
        const string DeniedTools = "Bash,PowerShell,Write,Edit,NotebookEdit,Read,Glob,Grep,WebFetch,WebSearch,Agent,Task,Skill,SendMessage,ListAgents,Monitor,CronCreate,CronDelete,CronList,RemoteTrigger,Workflow,ScheduleWakeup,PushNotification,Artifact,ArtifactData,ArtifactComments,TaskStop,EnterWorktree,ExitWorktree,EnterPlanMode,ExitPlanMode,DesignSync,ToolSearch,AskUserQuestion";
        const string AiContext =
            "You are a Windows performance assistant built into the app \"Task Manager Lite\". " +
            "The user runs VS Code with Claude Code, the Claude and ChatGPT desktop apps, the Hermes agent (Python), and Quantman " +
            "(an algo-trading web app) in Google Chrome on a 16 GB Windows 10 PC. They want everything to run smoothly without lag, " +
            "and the Quantman tab must stay reliable.\n" +
            "Answer the question using the system snapshot below. Be specific (name processes and numbers), practical and short: " +
            "at most about 12 bullet points, plain language, no tables. Prefer safe actions and warn before anything that could lose work " +
            "or interrupt trading. You cannot run commands; just answer.";

        readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        readonly string dataDir, settingsPath, srcFile;
        readonly bool startMinimized, justUpdated;

        readonly Sampler sampler = new Sampler();
        readonly Tile tCpu, tMem, tCommit, tSys;
        readonly NavButton[] nav = new NavButton[5];
        readonly Control[] pages = new Control[5];   // 0 Processes, 1 Advisor, 2 Ask AI, 3 Settings, 4 Free up memory
        int page;

        // Free up memory page
        readonly MemBar memBar = new MemBar();
        readonly Label lblMemResult = new Label(), lblJunkResult = new Label(), lblMemNote = new Label();
        readonly CheckBox chkTrimChrome = UI.Chk("Include Chrome when trimming (not while trading)"),
                          chkAutoTrim = UI.Chk("Auto-trim background apps when RAM is above 85%");
        Button btnTrim, btnPurge, btnScan, btnClean;
        List<Junk> junk;
        bool trimming, junkBusy, junkScanned;
        string cleanMsg;
        DateTime lastAutoTrim = DateTime.MinValue;
        public bool StartPurge;   // --purge: clear cached memory right after an elevated restart

        // Processes page
        readonly TextBox txtFilter = new TextBox();
        readonly CheckBox chkGroup = UI.Chk("Group by app");
        readonly ComboBox cbInterval = new ComboBox();
        readonly DbListView lv = new DbListView();
        readonly Label detName = new Label(), detDesc = new Label(), detMeta = new Label();
        readonly Button btnEnd, btnPrio, btnLoc, btnAskProc, btnSearch;
        string detailKey;

        // Advisor page
        readonly Label lblHealth = new Label(), lblHealthTitle = new Label(), lblHealthSub = new Label();
        readonly CheckBox chkNotify = UI.Chk("Notify me about problems"), chkAutoLower = UI.Chk("Auto-lower background CPU hogs"),
                          chkBoost = UI.Chk("Give Chrome (Quantman) priority");
        readonly FlowLayoutPanel cards = new FlowLayoutPanel();
        readonly TextBox logBox = new TextBox();
        List<Finding> findings = new List<Finding>();

        // AI page
        readonly TextBox txtQ = new TextBox();
        readonly RichTextBox rtb = new RichTextBox();
        readonly Label lblAi = new Label();
        readonly Button btnAsk, btnCancel;
        Process aiProc;
        StringBuilder aiOut, aiErr;
        DateTime aiStart;
        string lastAnswer, lastQuestion;
        readonly System.Windows.Forms.Timer aiTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        // Settings page
        readonly ComboBox cbTheme = new ComboBox(), cbModel = new ComboBox();
        readonly CheckBox chkTop = UI.Chk("Always on top"), chkTray = UI.Chk("Minimize to the notification area (tray)"),
                          chkStartup = UI.Chk("Start with Windows (minimized)"), chkAutoUpdate = UI.Chk("Install updates automatically");
        readonly Label lblUpdate = new Label();

        // Shell
        readonly Panel banner = new Panel();
        readonly Label bannerText = new Label();
        readonly Button bannerBtn;
        readonly Label lblStatus = new Label(), lblTip = new Label(), lblSideHealth = new Label();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer(), soon = new System.Windows.Forms.Timer(),
                                           updTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };

        // State
        List<Row> lastRows = new List<Row>();
        SysInfo lastSys;
        DateTime lastUpdate = DateTime.MinValue;
        int sortCol = 2;
        bool sortDesc = true, rendering, listDirty, loading, exiting, updating;
        int themeIdx;
        readonly List<double> cpuHist = new List<double>();
        class Pt { public DateTime T; public long V; }
        readonly Dictionary<string, List<Pt>> commitHist = new Dictionary<string, List<Pt>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> hungCount = new Dictionary<int, int>(), hogCount = new Dictionary<int, int>();
        readonly HashSet<int> boosted = new HashSet<int>(), lowered = new HashSet<int>();
        readonly Dictionary<string, DateTime> notified = new Dictionary<string, DateTime>();
        readonly List<string> logLines = new List<string>();
        string pendingVersion, lastUpdateError;

        public MainForm(bool startMinimized, bool justUpdated)
        {
            this.startMinimized = startMinimized;
            this.justUpdated = justUpdated;
            dataDir = Path.Combine(baseDir, "data");
            settingsPath = Path.Combine(baseDir, "settings.ini");
            srcFile = Path.Combine(baseDir, @"src\TaskManagerLite.cs");
            try { Directory.CreateDirectory(dataDir); } catch { }

            Text = AppInfo.Name;
            Font = F.Body;
            ClientSize = new Size(1200, 780);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---------- sidebar
            var side = new Panel { Dock = DockStyle.Left, Width = 210, Tag = "sidebar" };
            nav[0] = new NavButton("", "Processes");
            nav[1] = new NavButton("", "Smart Advisor");
            nav[2] = new NavButton("", "Ask AI");
            nav[3] = new NavButton("", "Settings");
            nav[4] = new NavButton("", "Free up memory");
            int[] navOrder = { 0, 1, 4, 2, 3 };   // order shown in the sidebar
            for (int k = navOrder.Length - 1; k >= 0; k--)
            {
                int idx = navOrder[k];
                nav[idx].Click += delegate { ShowPage(idx); };
                side.Controls.Add(nav[idx]);
            }
            side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 14, Tag = "sidebar" });
            side.Controls.Add(new Label { Text = "v" + AppInfo.Version + "  ·  lightweight", Dock = DockStyle.Top, Height = 22, Padding = new Padding(18, 0, 0, 0), Tag = "sideSub", Font = F.Small });
            side.Controls.Add(new Label { Text = AppInfo.Name, Dock = DockStyle.Top, Height = 50, Padding = new Padding(16, 0, 0, 4), TextAlign = ContentAlignment.BottomLeft, Tag = "side", Font = F.Side });
            lblSideHealth.Dock = DockStyle.Bottom; lblSideHealth.Height = 54; lblSideHealth.Padding = new Padding(18, 0, 10, 0);
            lblSideHealth.TextAlign = ContentAlignment.MiddleLeft; lblSideHealth.Tag = "sideSub"; lblSideHealth.Cursor = Cursors.Hand;
            lblSideHealth.Click += delegate { ShowPage(1); };
            side.Controls.Add(lblSideHealth);

            // ---------- tiles
            var tiles = new TableLayoutPanel { Dock = DockStyle.Top, Height = 146, ColumnCount = 4, RowCount = 1, Padding = new Padding(10, 10, 10, 0), Tag = "bg" };
            for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tCpu = new Tile("CPU", Color.FromArgb(17, 125, 187));
            tMem = new Tile("Memory", Color.FromArgb(139, 18, 174));
            tCommit = new Tile("Committed memory", Color.FromArgb(214, 122, 0));
            tSys = new Tile("System", Color.FromArgb(46, 160, 67)) { Graph = false };
            tiles.Controls.Add(tCpu, 0, 0); tiles.Controls.Add(tMem, 1, 0); tiles.Controls.Add(tCommit, 2, 0); tiles.Controls.Add(tSys, 3, 0);

            // ---------- update banner
            banner.Dock = DockStyle.Top; banner.Height = 44; banner.Visible = false; banner.Padding = new Padding(16, 8, 16, 6); banner.Tag = "banner";
            bannerBtn = UI.Btn("Install update", true);
            bannerBtn.Dock = DockStyle.Right;
            bannerBtn.Click += delegate { InstallUpdate(); };
            bannerText.Dock = DockStyle.Fill; bannerText.TextAlign = ContentAlignment.MiddleLeft; bannerText.Font = F.Bold;
            banner.Controls.Add(bannerText); banner.Controls.Add(bannerBtn);

            // ---------- status bar
            var status = new Panel { Dock = DockStyle.Bottom, Height = 28, Tag = "surface2" };
            lblStatus.Dock = DockStyle.Fill; lblStatus.TextAlign = ContentAlignment.MiddleLeft; lblStatus.Padding = new Padding(12, 0, 0, 0); lblStatus.Tag = "sub";
            lblTip.Dock = DockStyle.Right; lblTip.AutoSize = true; lblTip.Padding = new Padding(0, 6, 12, 0); lblTip.Tag = "warn";
            status.Controls.Add(lblStatus); status.Controls.Add(lblTip);

            // ---------- pages
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15, 8, 15, 10), Tag = "bg" };
            pages[0] = BuildProcessesPage(out btnEnd, out btnPrio, out btnLoc, out btnAskProc, out btnSearch);
            pages[1] = BuildAdvisorPage();
            pages[2] = BuildAiPage(out btnAsk, out btnCancel);
            pages[3] = BuildSettingsPage();
            pages[4] = BuildCleanupPage();
            foreach (var p in pages) { p.Dock = DockStyle.Fill; p.Visible = false; host.Controls.Add(p); }

            var content = new Panel { Dock = DockStyle.Fill, Tag = "bg" };
            content.Controls.Add(host);
            content.Controls.Add(status);
            content.Controls.Add(banner);
            content.Controls.Add(tiles);
            Controls.Add(content);
            Controls.Add(side);

            // ---------- tray
            tray.Icon = Icon;
            tray.Text = AppInfo.Name;
            tray.Visible = true;
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open " + AppInfo.Name, null, delegate { RestoreWindow(); });
            trayMenu.Items.Add("Smart Advisor", null, delegate { RestoreWindow(); ShowPage(1); });
            trayMenu.Items.Add("Ask AI", null, delegate { RestoreWindow(); ShowPage(2); });
            trayMenu.Items.Add("Free up memory", null, delegate { RestoreWindow(); ShowPage(4); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { RestoreWindow(); };
            tray.BalloonTipClicked += delegate { RestoreWindow(); ShowPage(1); };

            // ---------- events
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) { DoRefresh(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.F) { ShowPage(0); txtFilter.Focus(); e.Handled = true; }
            };
            timer.Tick += delegate { DoRefresh(); };
            soon.Tick += delegate { soon.Stop(); DoRefresh(); };
            aiTimer.Tick += delegate { AiTick(); };
            updTimer.Tick += delegate { CheckForUpdate(false); };
            Resize += delegate
            {
                if (exiting) return;
                if (WindowState == FormWindowState.Minimized && chkTray.Checked) Hide();
                else if (WindowState != FormWindowState.Minimized && listDirty && page == 0) RenderList();
            };
            FormClosing += delegate { SaveSettings(); CancelAi(false); };
            FormClosed += delegate { tray.Visible = false; tray.Dispose(); };

            loading = true;
            LoadSettings();
            loading = false;
            ApplyTheme();
            ShowPage(0);
            ShowDetails();
        }

        // =============================================================== Page builders
        Control BuildProcessesPage(out Button bEnd, out Button bPrio, out Button bLoc, out Button bAsk, out Button bSearch)
        {
            var pg = new Panel();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            txtFilter.Width = 240; txtFilter.Margin = new Padding(0, 4, 16, 0);
            txtFilter.HandleCreated += delegate { Native.SetCue(txtFilter, "Search processes (Ctrl+F)"); };
            bar.Controls.Add(txtFilter);
            bar.Controls.Add(chkGroup);
            bar.Controls.Add(UI.Lbl("Refresh every", "sub"));
            cbInterval.DropDownStyle = ComboBoxStyle.DropDownList; cbInterval.Width = 110; cbInterval.Margin = new Padding(0, 4, 10, 0);
            cbInterval.Items.AddRange(IntervalNames);
            bar.Controls.Add(cbInterval);
            var bRefresh = UI.Btn("Refresh (F5)", false);
            bRefresh.Margin = new Padding(0, 2, 8, 0);
            bRefresh.Click += delegate { DoRefresh(); };
            bar.Controls.Add(bRefresh);

            // list
            lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.HideSelection = false;
            lv.MultiSelect = true; lv.OwnerDraw = true; lv.BorderStyle = BorderStyle.FixedSingle;
            lv.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };   // taller rows
            lv.Columns.Add(ColumnNames[0], 300);
            lv.Columns.Add(ColumnNames[1], 90, HorizontalAlignment.Right);
            lv.Columns.Add(ColumnNames[2], 90, HorizontalAlignment.Right);
            lv.Columns.Add(ColumnNames[3], 120, HorizontalAlignment.Right);
            lv.Columns.Add(ColumnNames[4], 120, HorizontalAlignment.Right);
            lv.Columns.Add(ColumnNames[5], 120);
            lv.DrawColumnHeader += DrawHeader;
            lv.DrawItem += (s, e) => { e.DrawDefault = false; };
            lv.DrawSubItem += DrawCell;
            ListViewItem hover = null;
            lv.MouseMove += (s, e) =>
            {
                var it = lv.GetItemAt(e.X, e.Y);
                if (it == hover) return;
                if (hover != null && hover.ListView == lv) lv.Invalidate(hover.Bounds);
                if (it != null) lv.Invalidate(it.Bounds);
                hover = it;
            };
            bool fitting = false;
            Action fit = () =>
            {
                if (fitting || lv.Columns.Count < 6) return;
                fitting = true;
                int used = 0;
                for (int i = 0; i < 5; i++) used += lv.Columns[i].Width;
                lv.Columns[5].Width = Math.Max(100, lv.ClientSize.Width - used - 2);
                fitting = false;
            };
            lv.Resize += delegate { fit(); };
            lv.ColumnWidthChanged += delegate { fit(); };
            lv.ColumnClick += (s, e) =>
            {
                if (e.Column == sortCol) sortDesc = !sortDesc;
                else { sortCol = e.Column; sortDesc = e.Column >= 2 && e.Column <= 4; }
                RenderList();
            };
            lv.SelectedIndexChanged += delegate { if (!rendering) ShowDetails(); };
            lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) EndSelected(); };
            lv.DoubleClick += delegate { AskAboutSelected(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("End task", null, delegate { EndSelected(); });
            var prio = new ToolStripMenuItem("Set priority");
            AddPriority(prio, "Low", ProcessPriorityClass.Idle);
            AddPriority(prio, "Below normal", ProcessPriorityClass.BelowNormal);
            AddPriority(prio, "Normal", ProcessPriorityClass.Normal);
            AddPriority(prio, "Above normal", ProcessPriorityClass.AboveNormal);
            AddPriority(prio, "High", ProcessPriorityClass.High);
            menu.Items.Add(prio);
            menu.Items.Add("Ask AI about it", null, delegate { AskAboutSelected(); });
            menu.Items.Add("Open file location", null, delegate { OpenLocation(); });
            menu.Items.Add("Search online", null, delegate { SearchOnline(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy name", null, delegate { var r = SelectedRows(); if (r.Count > 0) Clipboard.SetText(r[0].Name); });
            menu.Opening += (s, e) => { if (lv.SelectedItems.Count == 0) e.Cancel = true; };
            lv.ContextMenuStrip = menu;

            // details panel
            var det = new Panel { Dock = DockStyle.Bottom, Height = 128, Padding = new Padding(16, 10, 16, 6), Tag = "surface" };
            var gap = new Panel { Dock = DockStyle.Bottom, Height = 10 };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            bEnd = UI.Btn("End task", false); bEnd.Click += delegate { EndSelected(); };
            bPrio = UI.Btn("Lower priority", false); bPrio.Click += delegate { SetPriority(ProcessPriorityClass.BelowNormal); };
            bAsk = UI.Btn("Ask AI about it", true); bAsk.Click += delegate { AskAboutSelected(); };
            bLoc = UI.Btn("Open file location", false); bLoc.Click += delegate { OpenLocation(); };
            bSearch = UI.Btn("Search online", false); bSearch.Click += delegate { SearchOnline(); };
            btns.Controls.AddRange(new Control[] { bAsk, bEnd, bPrio, bLoc, bSearch });
            detName.Dock = DockStyle.Top; detName.Height = 26; detName.Font = F.H2; detName.AutoEllipsis = true;
            detDesc.Dock = DockStyle.Top; detDesc.Height = 22; detDesc.AutoEllipsis = true;
            detMeta.Dock = DockStyle.Top; detMeta.Height = 22; detMeta.AutoEllipsis = true; detMeta.Tag = "sub";
            det.Controls.Add(btns); det.Controls.Add(detMeta); det.Controls.Add(detDesc); det.Controls.Add(detName);

            pg.Controls.Add(lv);
            pg.Controls.Add(gap);
            pg.Controls.Add(det);
            pg.Controls.Add(bar);

            chkGroup.CheckedChanged += delegate { if (!loading) RenderList(); };
            txtFilter.TextChanged += delegate { RenderList(); };
            cbInterval.SelectedIndexChanged += delegate { ApplyInterval(); };
            return pg;
        }

        Control BuildAdvisorPage()
        {
            var pg = new Panel();
            var head = new Panel { Dock = DockStyle.Top, Height = 92, Tag = "surface" };
            lblHealth.Font = F.Big; lblHealth.AutoSize = true; lblHealth.Location = new Point(18, 14);
            lblHealthTitle.Font = F.H2; lblHealthTitle.AutoSize = true; lblHealthTitle.Location = new Point(120, 20);
            lblHealthSub.AutoSize = true; lblHealthSub.Location = new Point(121, 48); lblHealthSub.Tag = "sub";
            var bCheck = UI.Btn("Run full check", true);
            bCheck.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bCheck.Click += delegate { DoRefresh(); Log("Full check: " + findings.Count(f => f.Severity >= Sev.Warning) + " problem(s), " + findings.Count(f => f.Severity == Sev.Info) + " tip(s)."); };
            head.Controls.Add(lblHealth); head.Controls.Add(lblHealthTitle); head.Controls.Add(lblHealthSub); head.Controls.Add(bCheck);
            head.Resize += delegate { bCheck.Location = new Point(head.Width - bCheck.Width - 18, 28); };

            var opts = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, Padding = new Padding(2, 8, 0, 0) };
            opts.Controls.Add(UI.Lbl("Auto-troubleshoot:", "bold"));
            opts.Controls.Add(chkNotify); opts.Controls.Add(chkAutoLower); opts.Controls.Add(chkBoost);
            foreach (var c in new[] { chkNotify, chkAutoLower, chkBoost }) c.CheckedChanged += delegate { if (!loading) { SaveSettings(); if (c.Checked && c != chkNotify) DoRefresh(); } };

            cards.Dock = DockStyle.Fill; cards.FlowDirection = FlowDirection.TopDown; cards.WrapContents = false; cards.AutoScroll = true;
            cards.Padding = new Padding(0, 6, 0, 0);
            cards.Resize += delegate { LayoutCards(); };

            var logHead = new Label { Text = "Activity log", Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.BottomLeft, Font = F.Bold };
            logBox.Dock = DockStyle.Bottom; logBox.Height = 78; logBox.Multiline = true; logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical; logBox.Font = F.Mono; logBox.BorderStyle = BorderStyle.FixedSingle;

            pg.Controls.Add(cards);
            pg.Controls.Add(logHead);
            pg.Controls.Add(logBox);
            pg.Controls.Add(opts);
            pg.Controls.Add(head);
            return pg;
        }

        Control BuildAiPage(out Button bAsk, out Button bCancel)
        {
            var pg = new Panel();
            var head = new Label { Text = "Ask AI", Dock = DockStyle.Top, Height = 40, Font = F.H1, TextAlign = ContentAlignment.MiddleLeft };
            var info = new Label
            {
                Dock = DockStyle.Top, Height = 40, Tag = "sub",
                Text = "Sends a snapshot of CPU, memory, top apps and Smart Advisor findings to Claude Code, using your Claude subscription. " +
                       "No files, passwords or browsing history are included, and Claude can't run commands or change anything on this PC."
            };
            var chips = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            foreach (var q in new[] {
                "Why is my PC slow right now?",
                "What can I close to free RAM?",
                "How do I keep Quantman smooth in Chrome?",
                "Is anything wrong with my system?",
                "Optimize my setup for VS Code + Claude + Chrome" })
            {
                var b = UI.Btn(q, false);
                string question = q;
                b.Click += delegate { txtQ.Text = question; AskAi(question); };
                chips.Controls.Add(b);
            }

            var qRow = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(0, 4, 0, 6) };
            txtQ.Multiline = true; txtQ.Dock = DockStyle.Fill; txtQ.Font = F.Input; txtQ.ScrollBars = ScrollBars.Vertical;
            txtQ.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; AskAi(txtQ.Text); } };
            var qBtns = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 290, Padding = new Padding(10, 0, 0, 0) };
            bAsk = UI.Btn("Ask Claude  (Enter)", true); bAsk.Click += delegate { AskAi(txtQ.Text); };
            bCancel = UI.Btn("Cancel", false); bCancel.Enabled = false; bCancel.Click += delegate { CancelAi(true); };
            var bCopy = UI.Btn("Copy report", false); bCopy.Click += delegate { Clipboard.SetText(BuildReport()); lblAi.Text = "Report copied. Paste it into Claude or ChatGPT if you prefer the web."; };
            var bWeb = UI.Btn("Open claude.ai", false); bWeb.Click += delegate { OpenUrl("https://claude.ai/new"); };
            qBtns.Controls.AddRange(new Control[] { bAsk, bCancel, bCopy, bWeb });
            qRow.Controls.Add(txtQ); qRow.Controls.Add(qBtns);

            lblAi.Dock = DockStyle.Top; lblAi.Height = 26; lblAi.Tag = "sub"; lblAi.TextAlign = ContentAlignment.MiddleLeft;
            var answerHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 6, 10), Tag = "surface" };
            rtb.Dock = DockStyle.Fill; rtb.ReadOnly = true; rtb.BorderStyle = BorderStyle.None; rtb.DetectUrls = true;
            rtb.LinkClicked += (s, e) => OpenUrl(e.LinkText);
            answerHost.Controls.Add(rtb);

            pg.Controls.Add(answerHost);
            pg.Controls.Add(lblAi);
            pg.Controls.Add(qRow);
            pg.Controls.Add(chips);
            pg.Controls.Add(info);
            pg.Controls.Add(head);
            return pg;
        }

        Control BuildSettingsPage()
        {
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8, 4, 8, 8) };
            Func<string, Label> section = t => new Label { Text = t, Font = F.H2, AutoSize = true, Margin = new Padding(0, 14, 0, 6) };
            Func<string, Control, Control> row = (label, ctl) =>
            {
                var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
                r.Controls.Add(new Label { Text = label, Width = 190, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
                ctl.Margin = new Padding(0, 3, 0, 0);
                r.Controls.Add(ctl);
                return r;
            };

            col.Controls.Add(new Label { Text = "Settings", Font = F.H1, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            col.Controls.Add(section("Appearance"));
            cbTheme.DropDownStyle = ComboBoxStyle.DropDownList; cbTheme.Width = 200;
            cbTheme.Items.AddRange(new object[] { "Match Windows", "Light", "Dark" });
            cbTheme.SelectedIndexChanged += delegate { if (!loading) { themeIdx = cbTheme.SelectedIndex; ApplyTheme(); SaveSettings(); } };
            col.Controls.Add(row("Theme", cbTheme));
            col.Controls.Add(chkTop);

            col.Controls.Add(section("Window and startup"));
            col.Controls.Add(chkTray);
            col.Controls.Add(chkStartup);

            col.Controls.Add(section("Ask AI"));
            cbModel.DropDownStyle = ComboBoxStyle.DropDownList; cbModel.Width = 200;
            cbModel.Items.AddRange(ModelNames);
            cbModel.SelectedIndexChanged += delegate { if (!loading) SaveSettings(); };
            col.Controls.Add(row("Claude model", cbModel));
            col.Controls.Add(new Label { Text = "Claude Code: " + (ClaudePath() ?? "not found – install Claude Code to use Ask AI"), AutoSize = true, Tag = "sub", Margin = new Padding(0, 4, 0, 0) });

            col.Controls.Add(section("Updates"));
            col.Controls.Add(chkAutoUpdate);
            lblUpdate.AutoSize = true; lblUpdate.MaximumSize = new Size(760, 0); lblUpdate.Tag = "sub"; lblUpdate.Margin = new Padding(0, 6, 0, 6);
            col.Controls.Add(lblUpdate);
            var ub = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var bCheck = UI.Btn("Check for updates", false); bCheck.Click += delegate { CheckForUpdate(true); };
            var bRebuild = UI.Btn("Rebuild now", false); bRebuild.Click += delegate { InstallUpdate(); };
            var bSrc = UI.Btn("Open program folder", false); bSrc.Click += delegate { Process.Start("explorer.exe", "\"" + baseDir + "\""); };
            ub.Controls.AddRange(new Control[] { bCheck, bRebuild, bSrc });
            col.Controls.Add(ub);

            col.Controls.Add(section("About"));
            col.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(760, 0), Tag = "sub",
                Text = AppInfo.Name + " v" + AppInfo.Version + " · Vk Professional Service\n" +
                       "Reads all processes with one Windows call per refresh (the method System Informer uses), so it stays light.\n" +
                       "To update: edit src\\TaskManagerLite.cs (raise Version), and the app rebuilds itself from that folder."
            });

            foreach (var c in new[] { chkTop, chkTray, chkStartup, chkAutoUpdate })
                c.CheckedChanged += delegate { if (!loading) { TopMost = chkTop.Checked; SetStartup(chkStartup.Checked); SaveSettings(); } };

            var pg = new Panel();
            col.Dock = DockStyle.Fill;
            pg.Controls.Add(col);
            return pg;
        }

        // =============================================================== Free up memory page
        Control BuildCleanupPage()
        {
            var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8, 4, 8, 8) };
            Func<string, Label> section = t => new Label { Text = t, Font = F.H2, AutoSize = true, Margin = new Padding(0, 14, 0, 6) };
            Func<Label, string, Label> note = (l, t) => { l.Text = t; l.AutoSize = true; l.MaximumSize = new Size(860, 0); l.Tag = "sub"; l.Margin = new Padding(0, 2, 0, 8); return l; };

            col.Controls.Add(new Label { Text = "Free up memory", Font = F.H1, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

            // ---- RAM
            col.Controls.Add(section("Memory (RAM)"));
            memBar.Width = 860; memBar.Margin = new Padding(0, 2, 0, 4);
            col.Controls.Add(memBar);
            col.Controls.Add(note(lblMemNote,
                "“Cached” memory holds recently used files. It isn't wasted: Windows hands it to any app instantly and already counts it as available. " +
                "Clearing it rarely makes things faster and makes apps reload files from disk for a while, so use it only when something stutters.\n" +
                "“Trim background apps” moves idle memory of apps you aren't using out of RAM, making room for the app in front. " +
                "It skips Windows, the app you're using, this app and (by default) Chrome, so Quantman isn't touched."));
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            btnTrim = UI.Btn("Trim background apps", true);
            btnTrim.Click += delegate { TrimBackground(); };
            btnPurge = UI.Btn(Native.IsElevated() ? "Clear cached memory" : "Clear cached memory (admin)", false);
            btnPurge.Click += delegate { ClearCache(); };
            row.Controls.Add(btnTrim);
            row.Controls.Add(btnPurge);
            col.Controls.Add(row);
            col.Controls.Add(chkTrimChrome);
            col.Controls.Add(chkAutoTrim);
            foreach (var c in new[] { chkTrimChrome, chkAutoTrim }) c.CheckedChanged += delegate { if (!loading) SaveSettings(); };
            lblMemResult.AutoSize = true; lblMemResult.MaximumSize = new Size(860, 0); lblMemResult.Tag = "bold"; lblMemResult.Margin = new Padding(0, 10, 0, 4);
            col.Controls.Add(lblMemResult);

            // ---- junk files
            col.Controls.Add(section("Junk files on disk"));
            col.Controls.Add(note(new Label(), "Removes temporary files older than a day, crash dumps and old error reports. Files that are in use are skipped automatically. " +
                                               "Nothing in Documents, Downloads or app settings is touched."));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            junk = new List<Junk> {
                new Junk { Name = "Temporary files (yours)", Paths = new[] { Path.GetTempPath() }, OlderThanDay = true },
                new Junk { Name = "Windows temporary files", Paths = new[] { Path.Combine(win, "Temp") }, OlderThanDay = true, Admin = true },
                new Junk { Name = "App crash dumps", Paths = new[] { Path.Combine(local, "CrashDumps") } },
                new Junk { Name = "Windows error reports", Paths = new[] { Path.Combine(local, @"Microsoft\Windows\WER"), Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue") } },
                new Junk { Name = "Recycle Bin", Recycle = true, DefaultOn = false } };
            bool elevated = Native.IsElevated();
            foreach (var j in junk)
            {
                j.Box = UI.Chk(j.Name + "   (scanning…)");
                j.Box.Checked = j.DefaultOn && (!j.Admin || elevated);
                j.Box.Enabled = !j.Admin || elevated;
                j.Box.Margin = new Padding(0, 4, 0, 0);
                col.Controls.Add(j.Box);
            }
            var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 2) };
            btnClean = UI.Btn("Clean selected", true);
            btnClean.Click += delegate { CleanJunk(); };
            btnScan = UI.Btn("Scan again", false);
            btnScan.Click += delegate { ScanJunk(); };
            row2.Controls.Add(btnClean);
            row2.Controls.Add(btnScan);
            col.Controls.Add(row2);
            lblJunkResult.AutoSize = true; lblJunkResult.MaximumSize = new Size(860, 0); lblJunkResult.Tag = "bold"; lblJunkResult.Margin = new Padding(0, 8, 0, 4);
            col.Controls.Add(lblJunkResult);

            col.Resize += delegate
            {
                int w = Math.Max(500, col.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 24);
                memBar.Width = w;
                foreach (var l in new[] { lblMemNote, lblMemResult, lblJunkResult }) l.MaximumSize = new Size(w, 0);
            };
            var pg = new Panel();
            pg.Controls.Add(col);
            return pg;
        }

        void UpdateMemoryView()
        {
            var s = lastSys;
            var m = Native.QueryMemoryLists();
            if (s == null || m == null) return;
            memBar.Total = (long)s.MemTotal;
            memBar.Standby = m.Standby;
            memBar.Modified = m.Modified;
            memBar.Free = m.Free + m.Zero;
            memBar.InUse = Math.Max(0, memBar.Total - memBar.Standby - memBar.Modified - memBar.Free);
            memBar.Invalidate();
        }

        void TrimBackground()
        {
            if (trimming) return;
            trimming = true;
            btnTrim.Enabled = false;
            lblMemResult.Text = "Trimming background apps…";
            var rows = lastRows.ToList();
            int fg = Native.ForegroundPid();
            int self;
            using (var me = Process.GetCurrentProcess()) self = me.Id;
            string fgName = rows.Where(r => r.Pid == fg).Select(r => r.Name).FirstOrDefault();
            if (string.Equals(fgName, "TaskManagerLite", StringComparison.OrdinalIgnoreCase)) fgName = null;
            bool incChrome = chkTrimChrome.Checked;
            var targets = rows.Where(r => r.WorkingSet >= 20L * 1048576 && r.Pid > 4 && r.Pid != self && !Protected.Contains(r.Name) &&
                                          !string.Equals(r.Name, fgName, StringComparison.OrdinalIgnoreCase) &&
                                          (incChrome || !r.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase))).ToList();
            long before = targets.Sum(r => r.WorkingSet);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int ok = 0;
                foreach (var t in targets)
                {
                    IntPtr h = Native.OpenProcess(0x0400 | 0x0100, false, t.Pid);   // QUERY_INFORMATION | SET_QUOTA
                    if (h == IntPtr.Zero) continue;
                    if (Native.EmptyWorkingSet(h)) ok++;
                    Native.CloseHandle(h);
                }
                Thread.Sleep(800);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        DoRefresh();
                        var ids = new HashSet<int>(targets.Select(x => x.Pid));
                        long after = lastRows.Where(r => ids.Contains(r.Pid)).Sum(r => r.WorkingSet);
                        long freed = Math.Max(0, before - after);
                        string skipped = "Windows processes" + (fgName != null ? ", " + fgName + " (in use)" : "") + (incChrome ? "" : ", Chrome");
                        lblMemResult.Text = string.Format("Trimmed {0} background process(es): {1:#,0} MB moved out of RAM. Skipped: {2}. " +
                                                          "Apps refill memory as you use them; that's normal.", ok, freed / 1048576.0, skipped);
                        Log(string.Format("Trimmed {0} background process(es), {1:#,0} MB moved out of RAM.", ok, freed / 1048576.0));
                        trimming = false;
                        btnTrim.Enabled = true;
                        UpdateMemoryView();
                    }));
                }
                catch { trimming = false; }
            });
        }

        void ClearCache()
        {
            if (!Native.IsElevated())
            {
                if (MessageBox.Show(this, "Clearing cached memory needs administrator rights.\n\n" + AppInfo.Name +
                        " will restart as administrator (Windows asks you to confirm) and then clear the cache.\n\nContinue?",
                        "Clear cached memory", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                RelaunchAsAdmin("--purge");
                return;
            }
            var before = Native.QueryMemoryLists();
            int st = Native.PurgeStandby();
            var after = Native.QueryMemoryLists();
            if (st != 0)
            {
                lblMemResult.Text = st == -1 ? "Windows didn't grant the permission needed to clear the cache."
                                             : "Windows refused to clear the cache (code 0x" + st.ToString("X") + ").";
                return;
            }
            long freed = before != null && after != null ? Math.Max(0, before.Standby - after.Standby) : 0;
            lblMemResult.Text = string.Format("Cleared {0:0.0} GB of cached memory. Apps may open files a little slower until Windows rebuilds the cache.", Gb(freed));
            Log(string.Format("Cleared {0:0.0} GB of cached (standby) memory.", Gb(freed)));
            DoRefresh();
            UpdateMemoryView();
        }

        void RelaunchAsAdmin(string extra)
        {
            int pid;
            using (var me = Process.GetCurrentProcess()) pid = me.Id;
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--wait " + pid + " --page 4 " + extra)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = baseDir
            };
            try { Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { lblMemResult.Text = "Cancelled: administrator permission wasn't given."; return; }
            SaveSettings();
            exiting = true;
            Close();
        }

        // Walks a folder tree without stopping at folders it can't read, and never follows junctions.
        static void Walk(string root, Action<FileInfo> onFile, List<string> dirs)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var di = new DirectoryInfo(stack.Pop());
                try { foreach (var f in di.EnumerateFiles()) { try { onFile(f); } catch { } } } catch { }
                try
                {
                    foreach (var s in di.EnumerateDirectories())
                        if ((s.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            stack.Push(s.FullName);
                            if (dirs != null) dirs.Add(s.FullName);
                        }
                }
                catch { }
            }
        }

        void ScanJunk()
        {
            if (junkBusy) return;
            junkBusy = true;
            junkScanned = true;
            btnScan.Enabled = btnClean.Enabled = false;
            lblJunkResult.Text = "Scanning…";
            var cutoff = DateTime.UtcNow.AddDays(-1);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var j in junk)
                {
                    long bytes = 0;
                    int files = 0;
                    if (j.Recycle)
                    {
                        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                        if (Native.SHQueryRecycleBin(null, ref info) == 0) { bytes = info.Size; files = (int)info.Items; }
                    }
                    else
                        foreach (var p in j.Paths)
                            Walk(p, f => { if (!j.OlderThanDay || f.LastWriteTimeUtc < cutoff) { bytes += f.Length; files++; } }, null);
                    j.Bytes = bytes;
                    j.Files = files;
                }
                try { BeginInvoke((Action)ShowJunk); } catch { }
            });
        }

        void ShowJunk()
        {
            bool elevated = Native.IsElevated();
            long total = 0;
            foreach (var j in junk)
            {
                string size = j.Bytes >= 1073741824 ? (j.Bytes / 1073741824.0).ToString("0.0") + " GB" : (j.Bytes / 1048576.0).ToString("#,0") + " MB";
                j.Box.Text = string.Format("{0}   —   {1}, {2:#,0} file(s){3}", j.Name, size, j.Files,
                    j.Admin && !elevated ? "   (needs admin: use “Clear cached memory (admin)” once to restart as admin)" : "");
                if (j.Box.Checked) total += j.Bytes;
            }
            lblJunkResult.Text = (cleanMsg != null ? cleanMsg + "   " : "") + string.Format("Selected now: about {0:#,0} MB can be removed.", total / 1048576.0);
            cleanMsg = null;
            junkBusy = false;
            btnScan.Enabled = btnClean.Enabled = true;
        }

        void CleanJunk()
        {
            if (junkBusy) return;
            var chosen = junk.Where(j => j.Box.Checked && j.Box.Enabled).ToList();
            if (chosen.Count == 0) { lblJunkResult.Text = "Tick at least one item first."; return; }
            if (chosen.Any(j => j.Recycle) &&
                MessageBox.Show(this, "Empty the Recycle Bin? Files in it can't be restored afterwards.", "Clean junk files",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            junkBusy = true;
            btnScan.Enabled = btnClean.Enabled = false;
            lblJunkResult.Text = "Cleaning…";
            IntPtr hwnd = Handle;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long freed = 0;
                int deleted = 0, skipped = 0;
                foreach (var j in chosen)
                {
                    if (j.Recycle)
                    {
                        if (Native.SHEmptyRecycleBin(hwnd, null, 0x7) == 0) { freed += j.Bytes; deleted += j.Files; }   // no confirm, no progress, no sound
                        continue;
                    }
                    foreach (var root in j.Paths)
                    {
                        var dirs = new List<string>();
                        Walk(root, f =>
                        {
                            if (j.OlderThanDay && f.LastWriteTimeUtc >= cutoff) return;
                            long len = f.Length;
                            try { f.Delete(); freed += len; deleted++; }
                            catch { skipped++; }   // in use or protected
                        }, dirs);
                        // remove folders that are now empty (deepest first); never the root itself
                        foreach (var d in dirs.OrderByDescending(x => x.Length))
                        {
                            try
                            {
                                if ((!j.OlderThanDay || Directory.GetLastWriteTimeUtc(d) < cutoff) && !Directory.EnumerateFileSystemEntries(d).Any())
                                    Directory.Delete(d);
                            }
                            catch { }
                        }
                    }
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        string msg = string.Format("Freed {0:#,0} MB ({1:#,0} file(s) removed{2}).", freed / 1048576.0, deleted,
                            skipped > 0 ? string.Format(", {0:#,0} in use or protected were skipped", skipped) : "");
                        Log("Junk cleanup: " + msg);
                        junkBusy = false;
                        cleanMsg = msg;
                        ScanJunk();   // refresh the sizes; ShowJunk keeps the result message
                    }));
                }
                catch { junkBusy = false; }
            });
        }

        // =============================================================== Theme
        bool ResolveDark()
        {
            if (themeIdx == 1) return false;
            if (themeIdx == 2) return true;
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int && (int)v == 0;
            }
            catch { return false; }
        }

        void ApplyTheme()
        {
            T.Set(ResolveDark());
            BackColor = T.Bg;
            ForeColor = T.Text;
            StyleTree(this);
            if (IsHandleCreated) Native.DarkTitleBar(Handle, T.Dark);
            if (lv.IsHandleCreated) Native.SetWindowTheme(lv.Handle, T.Dark ? "DarkMode_Explorer" : "Explorer", null);
            foreach (var tb in new Control[] { logBox, txtQ, cards, rtb })
                if (tb.IsHandleCreated) Native.SetWindowTheme(tb.Handle, T.Dark ? "DarkMode_Explorer" : "Explorer", null);
            if (lastAnswer != null) RenderMarkdown(lastAnswer); else if (rtb.TextLength == 0) ShowAiWelcome();
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.DarkTitleBar(Handle, T.Dark);
        }

        void StyleTree(Control root)
        {
            foreach (Control k in root.Controls)
            {
                Style(k);
                if (!(k is ListView) && !(k is TextBoxBase)) StyleTree(k);
            }
        }

        void Style(Control k)
        {
            string tag = k.Tag as string;
            if (k is Tile || k is NavButton || k is MemBar) { k.Invalidate(); return; }
            if (k is Button)
            {
                var b = (Button)k;
                bool prim = tag == "primary";
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = prim ? T.Accent : T.Surface2;
                b.ForeColor = prim ? Color.White : T.Text;
                b.FlatAppearance.BorderColor = prim ? T.Accent : T.Border;
                b.FlatAppearance.MouseOverBackColor = prim ? T.Blend(T.Accent, Color.White, 0.15) : T.Border;
                b.FlatAppearance.MouseDownBackColor = prim ? T.Blend(T.Accent, Color.Black, 0.15) : T.Border;
                return;
            }
            if (k is TextBoxBase) { k.BackColor = k is RichTextBox ? T.Surface : T.Input; k.ForeColor = T.Text; return; }
            if (k is ComboBox) { ((ComboBox)k).FlatStyle = FlatStyle.Flat; k.BackColor = T.Input; k.ForeColor = T.Text; return; }
            if (k is ListView) { k.BackColor = T.Surface; k.ForeColor = T.Text; return; }
            if (k is CheckBox) { k.ForeColor = T.Text; return; }
            if (k is Label)
            {
                switch (tag)
                {
                    case "sub": k.ForeColor = T.Sub; break;
                    case "warn": k.ForeColor = T.Red; break;
                    case "side": k.ForeColor = T.SideText; break;
                    case "sideSub": k.ForeColor = T.SideSub; break;
                    case "bold": k.ForeColor = T.Text; k.Font = F.Bold; break;
                    default: k.ForeColor = T.Text; break;
                }
                k.BackColor = tag == "side" || tag == "sideSub" ? T.Sidebar : ParentColor(k);
                return;
            }
            switch (tag)
            {
                case "sidebar": k.BackColor = T.Sidebar; break;
                case "surface": k.BackColor = T.Surface; break;
                case "surface2": k.BackColor = T.Surface2; break;
                case "bg": k.BackColor = T.Bg; break;
                case "banner": k.BackColor = T.Dark ? Color.FromArgb(22, 52, 84) : Color.FromArgb(222, 237, 252); break;
                default: k.BackColor = ParentColor(k); break;
            }
        }

        // Children take the parent's (already themed) color instead of Transparent, which is cheaper to draw.
        Color ParentColor(Control k)
        {
            return k.Parent != null ? k.Parent.BackColor : T.Bg;
        }

        // =============================================================== Navigation
        void ShowPage(int i)
        {
            page = i;
            for (int k = 0; k < pages.Length; k++)
            {
                pages[k].Visible = k == i;
                nav[k].Selected = k == i;
                nav[k].Invalidate();
            }
            if (i == 0 && listDirty) RenderList();
            if (i == 1) RenderCards(true);
            if (i == 2) txtQ.Focus();
            if (i == 3) UpdateUpdateLabel();
            if (i == 4) { UpdateMemoryView(); if (!junkScanned) ScanJunk(); }
        }

        void RestoreWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.DarkTitleBar(Handle, T.Dark);
            DoRefresh();          // first sample (baseline)
            soon.Interval = 1500; // second sample soon, so CPU % appears quickly
            soon.Start();
            ApplyInterval();
            updTimer.Start();
            var t = new System.Windows.Forms.Timer { Interval = 4000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); CheckForUpdate(false); };
            t.Start();
            if (justUpdated)
            {
                Log("Updated to version " + AppInfo.Version + ".");
                tray.ShowBalloonTip(4000, AppInfo.Name, "Updated to version " + AppInfo.Version + ".", ToolTipIcon.Info);
            }
            if (StartPage > 0 && StartPage < pages.Length) ShowPage(StartPage);
            if (StartPurge)
            {
                ShowPage(4);
                var pt = new System.Windows.Forms.Timer { Interval = 1200 };
                pt.Tick += delegate { pt.Stop(); pt.Dispose(); ClearCache(); };
                pt.Start();
            }
            if (!string.IsNullOrEmpty(StartQuestion))
            {
                // wait for the second sample so the snapshot has CPU numbers
                var q = new System.Windows.Forms.Timer { Interval = 2500 };
                q.Tick += delegate { q.Stop(); q.Dispose(); ShowPage(2); txtQ.Text = StartQuestion; AskAi(StartQuestion); };
                q.Start();
            }
            if (startMinimized) WindowState = FormWindowState.Minimized;
        }

        public int StartPage;
        public string StartQuestion;   // --ask "question": open Ask AI and ask it (handy for shortcuts)

        // =============================================================== Refresh loop
        static double Gb(double bytes) { return bytes / 1073741824.0; }
        int IntervalSeconds { get { return IntervalSecs[Math.Max(0, cbInterval.SelectedIndex)]; } }

        void ApplyInterval()
        {
            timer.Stop();
            if (IntervalSeconds > 0) { timer.Interval = IntervalSeconds * 1000; timer.Start(); }
            if (!loading) SaveSettings();
            UpdateStatus();
        }

        void RefreshSoon()
        {
            soon.Stop();
            soon.Interval = 800;
            soon.Start();
        }

        void DoRefresh()
        {
            var s = sampler.SampleSystem();
            lastSys = s;
            if (s.Cpu >= 0) { cpuHist.Add(s.Cpu); if (cpuHist.Count > 12) cpuHist.RemoveAt(0); }
            lastRows = sampler.SampleProcesses();
            TrackHistory();

            tCpu.Value = s.Cpu >= 0 ? s.Cpu.ToString("0") + "%" : "…";
            tCpu.Sub = Environment.ProcessorCount + " logical processors";
            tCpu.Percent = s.Cpu; tCpu.Push(s.Cpu);
            tMem.Value = string.Format("{0:0.0} / {1:0.0} GB", Gb(s.MemTotal - s.MemAvail), Gb(s.MemTotal));
            tMem.Sub = string.Format("{0:0}% used · {1:0.0} GB available", s.MemPct, Gb(s.MemAvail));
            tMem.Percent = s.MemPct; tMem.Push(s.MemPct);
            tCommit.Value = string.Format("{0:0.0} / {1:0.0} GB", Gb(s.CommitTotal), Gb(s.CommitLimit));
            tCommit.Sub = string.Format("{0:0}% of limit · cache {1:0.0} GB", s.CommitPct, Gb(s.Cache));
            tCommit.Percent = s.CommitPct; tCommit.Push(s.CommitPct);
            tSys.Value = s.Processes + " processes";
            tSys.Sub = string.Format("{0:#,0} threads · {1:#,0} handles", s.Threads, s.Handles);
            tSys.Sub2 = "Up " + Uptime();
            tCpu.Invalidate(); tMem.Invalidate(); tCommit.Invalidate(); tSys.Invalidate();

            string summary = s.Cpu >= 0 ? string.Format("CPU {0:0}% · RAM {1:0}% · Commit {2:0}%", s.Cpu, s.MemPct, s.CommitPct) : "";
            Text = summary.Length > 0 ? AppInfo.Name + " – " + summary : AppInfo.Name;
            tray.Text = summary.Length > 0 ? summary : AppInfo.Name;

            bool visible = Visible && WindowState != FormWindowState.Minimized;
            if (visible && page == 0) RenderList(); else listDirty = true;

            AutoActions();
            findings = Diagnose();
            UpdateHealth();
            if (visible && page == 1) RenderCards(false);
            if (visible && page == 4) UpdateMemoryView();
            Notify();

            lastUpdate = DateTime.Now;
            UpdateStatus();
        }

        static string Uptime()
        {
            var t = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            return t.Days > 0 ? string.Format("{0}d {1}h {2}m", t.Days, t.Hours, t.Minutes) : string.Format("{0}h {1}m", t.Hours, t.Minutes);
        }

        void UpdateStatus()
        {
            long own;
            using (var me = Process.GetCurrentProcess()) own = me.WorkingSet64;
            lblStatus.Text = string.Format("{0}   ·   {1}   ·   {2} uses {3:0} MB RAM",
                lastUpdate == DateTime.MinValue ? "Starting…" : "Updated " + lastUpdate.ToString("HH:mm:ss"),
                IntervalSeconds > 0 ? "refreshes every " + IntervalSeconds + " s" : "auto-refresh paused (F5 to refresh)",
                AppInfo.Name, own / 1048576.0);
            var top = findings.FirstOrDefault(f => f.Severity >= Sev.Warning);
            lblTip.Text = top != null ? "⚠ " + top.Title : "";
        }

        // =============================================================== Process list
        static List<Row> Group(List<Row> src)
        {
            var map = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in src)
            {
                Row g;
                if (!map.TryGetValue(r.Name, out g))
                {
                    g = new Row { Name = r.Name, Key = "n:" + r.Name.ToLowerInvariant(), Pid = -1, Count = 0, Priority = r.Priority };
                    map[r.Name] = g;
                }
                g.Count++;
                g.WorkingSet += r.WorkingSet;
                g.Private += r.Private;
                g.Pids.Add(r.Pid);
                if (r.CpuKnown) { g.Cpu += r.Cpu; g.CpuKnown = true; }
                if (g.Priority != r.Priority) g.Priority = "Mixed";
            }
            return map.Values.ToList();
        }

        int CompareRows(Row a, Row b)
        {
            int c;
            switch (sortCol)
            {
                case 0: c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: c = chkGroup.Checked ? a.Count.CompareTo(b.Count) : a.Pid.CompareTo(b.Pid); break;
                case 2: c = (a.CpuKnown ? a.Cpu : -1).CompareTo(b.CpuKnown ? b.Cpu : -1); break;
                case 3: c = a.WorkingSet.CompareTo(b.WorkingSet); break;
                case 4: c = a.Private.CompareTo(b.Private); break;
                default: c = string.Compare(a.Priority, b.Priority, StringComparison.OrdinalIgnoreCase); break;
            }
            if (sortDesc) c = -c;
            if (c == 0) c = b.WorkingSet.CompareTo(a.WorkingSet);
            if (c == 0) c = a.Pid.CompareTo(b.Pid);
            return c;
        }

        void RenderList()
        {
            listDirty = false;
            bool grouped = chkGroup.Checked;
            List<Row> rows = grouped ? Group(lastRows) : new List<Row>(lastRows);
            string f = txtFilter.Text.Trim();
            if (f.Length > 0)
                rows = rows.Where(r => r.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || r.Pid.ToString() == f).ToList();
            rows.Sort(CompareRows);

            var selected = new HashSet<string>();
            foreach (ListViewItem it in lv.SelectedItems) selected.Add(it.Name);
            int topIndex = lv.TopItem != null ? lv.TopItem.Index : 0;

            rendering = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            for (int c = 0; c < ColumnNames.Length; c++)
            {
                string name = c == 1 && grouped ? "Processes" : ColumnNames[c];
                lv.Columns[c].Text = c == sortCol ? name + (sortDesc ? "  ▼" : "  ▲") : name;
            }
            var items = new ListViewItem[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var it = new ListViewItem(r.Name) { Name = r.Key, Tag = r };
                it.SubItems.Add(grouped ? r.Count.ToString() : r.Pid.ToString());
                it.SubItems.Add(r.CpuKnown ? r.Cpu.ToString("0.0") : "–");
                it.SubItems.Add((r.WorkingSet / 1048576.0).ToString("#,0"));
                it.SubItems.Add((r.Private / 1048576.0).ToString("#,0"));
                it.SubItems.Add(r.Priority);
                items[i] = it;
            }
            lv.Items.AddRange(items);
            foreach (var it in items) if (selected.Contains(it.Name)) it.Selected = true;
            lv.EndUpdate();
            if (topIndex > 0 && topIndex < items.Length) { try { lv.TopItem = items[topIndex]; } catch { } }
            rendering = false;
            ShowDetails();
        }

        void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(T.Surface2)) g.FillRectangle(b, e.Bounds);
            using (var p = new Pen(T.Border))
            {
                g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                g.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
            }
            bool right = e.ColumnIndex >= 1 && e.ColumnIndex <= 4;
            var r = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height);
            TextRenderer.DrawText(g, e.Header.Text, F.Bold, r, e.ColumnIndex == sortCol ? T.Text : T.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
        }

        void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            var row = e.Item.Tag as Row;
            var g = e.Graphics;
            var b = e.Bounds;
            if (e.ColumnIndex == 0) b = new Rectangle(b.X, b.Y, lv.Columns[0].Width, b.Height);
            Color bg = T.Surface;
            if (e.Item.Selected) bg = T.Sel;
            else if (row != null)
            {
                double f = 0;
                if (e.ColumnIndex == 2 && row.CpuKnown) f = row.Cpu / 40.0;
                else if (e.ColumnIndex == 3) f = row.WorkingSet / 1610612736.0;   // 1.5 GB
                else if (e.ColumnIndex == 4) f = row.Private / 3221225472.0;      // 3 GB
                if (f > 0.04) bg = T.Blend(T.Surface, T.Heat, 0.12 + 0.68 * Math.Min(1, f));
            }
            using (var br = new SolidBrush(bg)) g.FillRectangle(br, b);
            bool right = e.ColumnIndex >= 1 && e.ColumnIndex <= 4;
            Color fc = T.Text;
            if (row != null && e.ColumnIndex == 0 && Protected.Contains(row.Name) && !e.Item.Selected) fc = T.Sub;
            if (row != null && e.ColumnIndex == 5 && row.Priority != "Normal" && row.Priority != "–") fc = T.Accent;
            var r = new Rectangle(b.X + 10, b.Y, b.Width - 20, b.Height);
            TextRenderer.DrawText(g, e.SubItem.Text, e.ColumnIndex == 0 ? F.Bold : lv.Font, r, fc,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
        }

        List<Row> SelectedRows()
        {
            var l = new List<Row>();
            foreach (ListViewItem it in lv.SelectedItems) l.Add((Row)it.Tag);
            return l;
        }

        void ShowDetails()
        {
            var rows = SelectedRows();
            bool any = rows.Count > 0;
            btnEnd.Enabled = btnPrio.Enabled = btnLoc.Enabled = btnAskProc.Enabled = btnSearch.Enabled = any;
            if (!any)
            {
                detailKey = null;
                detName.Text = "Select a process to see what it is";
                detDesc.Text = "Tip: tick “Group by app” to see the total for each app, for example all Chrome processes together.";
                detMeta.Text = "Yellow shading = heavy CPU or memory use. Grey names are core Windows processes. Double-click a row to ask AI about it.";
                return;
            }
            var r = rows[0];
            string key = string.Join(",", rows.Select(x => x.Key));
            var info = Knowledge.Get(r.Name);
            detName.Text = rows.Count > 1 ? rows.Count + " items selected"
                : r.Name + (r.Count > 1 ? "   ·   " + r.Count + " processes" : "   ·   PID " + r.Pid) +
                  string.Format("   ·   {0:0.0}% CPU   ·   {1:#,0} MB memory   ·   {2:#,0} MB commit", r.CpuKnown ? r.Cpu : 0, r.WorkingSet / 1048576.0, r.Private / 1048576.0);
            if (key == detailKey) return;   // the rest only changes with the selection
            detailKey = key;
            detDesc.Text = info != null ? info.Desc : "No built-in description for this one. Use “Ask AI about it” or “Search online”.";
            string safe = Protected.Contains(r.Name) ? "No – part of Windows" : info != null ? info.Safe : "Unknown";
            string path = null;
            foreach (int pid in r.Pids) { path = Native.ImagePath(pid); if (path != null) break; }
            detMeta.Text = "Safe to end: " + safe + "      Location: " + (path ?? "hidden by Windows");
        }

        // =============================================================== Process actions
        void AddPriority(ToolStripMenuItem parent, string text, ProcessPriorityClass pc)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += delegate { SetPriority(pc); };
            parent.DropDownItems.Add(it);
        }

        List<Row> AllowedRows(List<Row> rows, string action)
        {
            var blocked = rows.Where(r => Protected.Contains(r.Name)).Select(r => r.Name).Distinct().ToList();
            if (blocked.Count > 0)
                MessageBox.Show(this, string.Join(", ", blocked) + (blocked.Count == 1 ? " is" : " are") +
                    " part of Windows. Changing it can freeze or restart the PC, so " + AppInfo.Name + " won't " + action + " it.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return rows.Where(r => !Protected.Contains(r.Name)).ToList();
        }

        void ShowErrors(string action, List<string> errors)
        {
            if (errors.Count == 0) return;
            MessageBox.Show(this, "Windows didn't allow " + AppInfo.Name + " to " + action + " these processes:\n\n" +
                string.Join("\n", errors.Take(8)) + (errors.Count > 8 ? "\n…and " + (errors.Count - 8) + " more" : "") +
                "\n\n\"Access is denied\" means the process runs as administrator or as Windows itself. " +
                "To control it, right-click the " + AppInfo.Name + " shortcut and choose \"Run as administrator\".",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void EndSelected() { EndRows(SelectedRows()); }

        void EndRows(List<Row> selection)
        {
            if (selection.Count == 0) return;
            var rows = AllowedRows(selection, "end");
            if (rows.Count == 0) return;
            int total = rows.Sum(r => r.Pids.Count);
            string msg = total == 1
                ? string.Format("End \"{0}\" (PID {1})?", rows[0].Name, rows[0].Pids[0])
                : string.Format("End {0} processes ({1})?", total, string.Join(", ", rows.Select(r => r.Name).Distinct().Take(6)));
            if (rows.Any(r => r.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase)))
                msg += "\n\nThis can close Chrome tabs, including Quantman.";
            if (MessageBox.Show(this, msg + "\n\nAny unsaved work in it will be lost.", "End task",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var errors = new List<string>();
            int ok = 0;
            foreach (var r in rows)
                foreach (int pid in r.Pids)
                {
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                            if (string.Equals(p.ProcessName, r.Name, StringComparison.OrdinalIgnoreCase)) { p.Kill(); ok++; }
                    }
                    catch (ArgumentException) { }   // already closed
                    catch (Exception ex) { errors.Add(string.Format("{0} (PID {1}): {2}", r.Name, pid, ex.Message)); }
                }
            if (ok > 0) Log(string.Format("Ended {0} process(es): {1}.", ok, string.Join(", ", rows.Select(r => r.Name).Distinct())));
            ShowErrors("end", errors);
            RefreshSoon();
        }

        void SetPriority(ProcessPriorityClass pc)
        {
            if (lv.SelectedItems.Count == 0) return;
            var rows = AllowedRows(SelectedRows(), "change the priority of");
            if (rows.Count == 0) return;
            if (pc == ProcessPriorityClass.High &&
                MessageBox.Show(this, "High priority can make the rest of Windows slow to respond. Continue?", "Set priority",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var errors = new List<string>();
            int ok = 0;
            foreach (var r in rows)
                foreach (int pid in r.Pids)
                {
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                            if (string.Equals(p.ProcessName, r.Name, StringComparison.OrdinalIgnoreCase)) { p.PriorityClass = pc; ok++; }
                    }
                    catch (ArgumentException) { }
                    catch (Exception ex) { errors.Add(string.Format("{0} (PID {1}): {2}", r.Name, pid, ex.Message)); }
                }
            if (ok > 0) Log(string.Format("Set priority of {0} process(es) ({1}) to {2}. It resets when the app restarts.", ok, string.Join(", ", rows.Select(r => r.Name).Distinct()), pc));
            ShowErrors("change the priority of", errors);
            RefreshSoon();
        }

        void OpenLocation()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) return;
            string path = null;
            foreach (int pid in rows[0].Pids) { path = Native.ImagePath(pid); if (path != null) break; }
            if (path == null || !File.Exists(path))
            {
                MessageBox.Show(this, "Windows didn't allow reading where \"" + rows[0].Name + "\" is stored. It is usually a protected Windows process.",
                    "Open file location", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        void SearchOnline()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) return;
            OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString("\"" + rows[0].Name + ".exe\" what is this process"));
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        void AskAboutSelected()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) return;
            var r = rows[0];
            string path = null;
            foreach (int pid in r.Pids) { path = Native.ImagePath(pid); if (path != null) break; }
            string q = string.Format("What is \"{0}\"{1}? It has {2} process(es) using {3:0.0}% CPU, {4:#,0} MB memory and {5:#,0} MB commit, priority {6}. " +
                                     "Should I end it, lower its priority, or leave it alone given my apps?",
                r.Name, path != null ? " (" + path + ")" : "", r.Count, r.CpuKnown ? r.Cpu : 0, r.WorkingSet / 1048576.0, r.Private / 1048576.0, r.Priority);
            ShowPage(2);
            txtQ.Text = q;
            AskAi(q);
        }

        void ShowApps(string filter, bool group, int sortColumn)
        {
            loading = true;
            chkGroup.Checked = group;
            loading = false;
            sortCol = sortColumn;
            sortDesc = true;
            txtFilter.Text = filter;   // triggers RenderList
            ShowPage(0);
            RenderList();
        }

        // =============================================================== Smart Advisor
        void TrackHistory()
        {
            var now = DateTime.Now;
            var groups = Group(lastRows);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                names.Add(g.Name);
                List<Pt> l;
                if (!commitHist.TryGetValue(g.Name, out l)) { l = new List<Pt>(); commitHist[g.Name] = l; }
                l.Add(new Pt { T = now, V = g.Private });
                l.RemoveAll(p => (now - p.T).TotalMinutes > 15);
            }
            foreach (var k in commitHist.Keys.ToList()) if (!names.Contains(k)) commitHist.Remove(k);

            var hung = Native.HungWindowPids();
            var next = new Dictionary<int, int>();
            foreach (int pid in hung) { int c; hungCount.TryGetValue(pid, out c); next[pid] = c + 1; }
            hungCount = next;
        }

        List<Finding> Diagnose()
        {
            var list = new List<Finding>();
            var s = lastSys;
            if (s == null) return list;
            var groups = Group(lastRows);
            var byName = groups.ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
            Func<string, Row> app = n => { Row r; return byName.TryGetValue(n, out r) ? r : null; };
            string biggest = string.Join(", ", groups.Where(g => !Protected.Contains(g.Name)).OrderByDescending(g => g.Private).Take(4)
                .Select(g => string.Format("{0} {1:0.0} GB", g.Name, Gb(g.Private))));
            Action showBig = () => ShowApps("", true, 4);
            Action showCpu = () => ShowApps("", true, 2);

            // 1. Committed memory
            if (s.CommitPct >= 90)
                list.Add(new Finding { Id = "commit", Severity = Sev.Critical, Title = string.Format("Committed memory is almost full ({0:0}%)", s.CommitPct),
                    Detail = "Windows may close apps or show “out of memory” errors. Close an app you aren't using. The biggest are: " + biggest + ".",
                    ActionText = "Show biggest apps", Act = showBig });
            else if (s.CommitPct >= 80)
                list.Add(new Finding { Id = "commit", Severity = Sev.Warning, Title = string.Format("Committed memory is high ({0:0}%)", s.CommitPct),
                    Detail = "You are close to the limit, which causes lag and can crash apps. The biggest are: " + biggest + ".",
                    ActionText = "Show biggest apps", Act = showBig });

            // 2. Physical RAM
            if (s.MemPct >= 90)
                list.Add(new Finding { Id = "ram", Severity = Sev.Critical, Title = string.Format("RAM is almost full ({0:0}% used)", s.MemPct),
                    Detail = string.Format("Only {0:0.0} GB is free, so Windows is swapping to disk and everything slows down. Close apps you aren't using, or trim background apps.", Gb(s.MemAvail)),
                    ActionText = "Free up memory", Act = () => ShowPage(4) });
            else if (s.MemPct >= 80)
                list.Add(new Finding { Id = "ram", Severity = Sev.Warning, Title = string.Format("RAM is getting full ({0:0}% used)", s.MemPct),
                    Detail = string.Format("{0:0.0} GB free. Closing one large app, or trimming background apps, keeps things smooth.", Gb(s.MemAvail)),
                    ActionText = "Free up memory", Act = () => ShowPage(4) });

            // 3. Sustained CPU
            if (cpuHist.Count >= 3 && cpuHist.Skip(cpuHist.Count - 3).All(v => v >= 85))
            {
                string hogs = string.Join(", ", groups.Where(g => g.CpuKnown).OrderByDescending(g => g.Cpu).Take(3).Select(g => string.Format("{0} {1:0}%", g.Name, g.Cpu)));
                list.Add(new Finding { Id = "cpu", Severity = Sev.Warning, Title = string.Format("CPU has been busy for a while ({0:0}%)", cpuHist[cpuHist.Count - 1]),
                    Detail = "Top CPU users: " + hogs + ". Lowering the priority of a background app keeps Chrome and VS Code responsive.",
                    ActionText = "Show CPU users", Act = showCpu });
            }

            // 4. Not responding
            foreach (var kv in hungCount.Where(h => h.Value >= 2))
            {
                var r = lastRows.FirstOrDefault(x => x.Pid == kv.Key);
                if (r == null || Protected.Contains(r.Name)) continue;
                var target = r;
                list.Add(new Finding { Id = "hung:" + r.Pid, Severity = Sev.Warning, Title = r.Name + " is not responding",
                    Detail = "Its window stopped responding. Wait a minute; if it stays frozen, end it (unsaved work in it will be lost).",
                    ActionText = "End " + r.Name, Act = () => EndRows(new List<Row> { target }) });
            }

            // 5. Memory growing (possible leak)
            foreach (var kv in commitHist)
            {
                if (kv.Value.Count < 3 || Protected.Contains(kv.Key)) continue;
                var first = kv.Value[0];
                var last = kv.Value[kv.Value.Count - 1];
                double minutes = (last.T - first.T).TotalMinutes;
                long grow = last.V - first.V;
                if (minutes >= 3 && grow >= 700L * 1048576 && last.V >= 1073741824L)
                {
                    string name = kv.Key;
                    list.Add(new Finding { Id = "grow:" + name, Severity = Sev.Warning,
                        Title = string.Format("{0} keeps using more memory (+{1:#,0} MB in {2:0} min)", name, grow / 1048576.0, minutes),
                        Detail = "This often means a memory leak or a tab/task that keeps loading data. Restart it when convenient.",
                        ActionText = "Show " + name, Act = () => ShowApps(name, false, 4) });
                }
            }

            // 6. Chrome
            var chrome = app("chrome");
            if (chrome != null && chrome.Private >= 3L * 1073741824)
                list.Add(new Finding { Id = "chrome", Severity = Sev.Info, Title = string.Format("Chrome uses {0:0.0} GB across {1} processes", Gb(chrome.Private), chrome.Count),
                    Detail = "Turn on Memory Saver, and add your Quantman site under “Always keep these sites active” so the trading tab never sleeps. Close tabs you aren't using.",
                    ActionText = "Open Chrome performance settings", Act = () => { try { Process.Start("chrome.exe", "chrome://settings/performance"); } catch { } } });

            // 7. AI desktop apps
            var gpt = app("ChatGPT");
            var cl = app("claude");
            long aiApps = (gpt != null ? gpt.Private : 0) + (cl != null ? cl.Private : 0);
            if (gpt != null && aiApps >= 2L * 1073741824)
                list.Add(new Finding { Id = "aiapps", Severity = Sev.Info, Title = string.Format("ChatGPT and Claude apps use {0:0.0} GB", Gb(aiApps)),
                    Detail = "Using ChatGPT in a Chrome tab instead of the desktop app usually saves about 1 GB. If you use Claude Code in VS Code, you may not need the Claude desktop app open as well.",
                    ActionText = "Show them", Act = () => ShowApps("", true, 4) });

            // 8. Python / Hermes
            var py = app("python");
            if (py != null && py.Private >= 1536L * 1048576)
                list.Add(new Finding { Id = "python", Severity = Sev.Info, Title = string.Format("Python (Hermes?) holds {0:0.0} GB", Gb(py.Private)),
                    Detail = "If this is Hermes, restarting it now and then frees this memory. Lowering its priority keeps Chrome smooth when it is busy.",
                    ActionText = "Show python", Act = () => ShowApps("python", false, 4) });

            // 9. VS Code
            var code = app("Code");
            if (code != null && (code.Count >= 14 || code.Private >= 2L * 1073741824))
                list.Add(new Finding { Id = "vscode", Severity = Sev.Info, Title = string.Format("VS Code: {0} processes, {1:0.0} GB", code.Count, Gb(code.Private)),
                    Detail = "Close VS Code windows you aren't using, and disable extensions you don't need (Ctrl+Shift+X → Disable).",
                    ActionText = "Show VS Code", Act = () => ShowApps("Code", false, 4) });

            // 10. Defender
            var mp = app("MsMpEng");
            if (mp != null && mp.CpuKnown && mp.Cpu >= 15)
                list.Add(new Finding { Id = "defender", Severity = Sev.Info, Title = string.Format("Windows Defender is scanning ({0:0}% CPU)", mp.Cpu),
                    Detail = "This is usually temporary and settles on its own. Don't turn Defender off." });

            // 11. dwm
            var dwm = app("dwm");
            if (dwm != null && dwm.CpuKnown && dwm.Cpu >= 15)
                list.Add(new Finding { Id = "dwm", Severity = Sev.Warning, Title = string.Format("Desktop Window Manager uses {0:0}% CPU", dwm.Cpu),
                    Detail = "Update your graphics driver, and choose “Adjust for best performance” in Performance Options to reduce visual effects.",
                    ActionText = "Open Performance Options", Act = () => { try { Process.Start("SystemPropertiesPerformance.exe"); } catch { } } });

            // 12. Disk space
            try
            {
                var d = new DriveInfo("C");
                double freeGb = Gb(d.AvailableFreeSpace);
                if (d.IsReady && (freeGb < 15 || d.AvailableFreeSpace < d.TotalSize / 10))
                    list.Add(new Finding { Id = "disk", Severity = freeGb < 5 ? Sev.Critical : Sev.Warning, Title = string.Format("Drive C: is low on space ({0:0.0} GB free)", freeGb),
                        Detail = "Windows needs free space for the page file and updates. Use Storage Sense or delete large downloads.",
                        ActionText = "Open Storage settings", Act = () => OpenUrl("ms-settings:storagesense") });
            }
            catch { }

            // 13. Page file
            if (Gb(s.PageFile) < 8 && s.CommitPct >= 75)
                list.Add(new Finding { Id = "pagefile", Severity = Sev.Info, Title = string.Format("The page file is only about {0:0.0} GB", Gb(s.PageFile)),
                    Detail = "A bigger page file prevents out-of-memory crashes when many apps are open: Performance Options → Advanced → Virtual memory → Change → Custom size 16384 MB on C:. Needs admin rights and a restart.",
                    ActionText = "Open Performance Options", Act = () => { try { Process.Start("SystemPropertiesPerformance.exe"); } catch { } } });

            // 14. Uptime
            var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            if (up.TotalDays >= 7)
                list.Add(new Finding { Id = "uptime", Severity = Sev.Info, Title = string.Format("The PC hasn't restarted in {0:0} days", up.TotalDays),
                    Detail = "A restart clears leaked memory and finishes pending updates. Do it outside trading hours." });

            // 15. Update problems
            if (lastUpdateError != null)
                list.Add(new Finding { Id = "update", Severity = Sev.Warning, Title = "The last update failed to build",
                    Detail = lastUpdateError, ActionText = "Open update log", Act = () => { try { Process.Start("notepad.exe", "\"" + Path.Combine(dataDir, "update.log") + "\""); } catch { } } });

            if (list.Count == 0)
                list.Add(new Finding { Id = "ok", Severity = Sev.Good, Title = "Everything looks good",
                    Detail = "CPU, memory, disk and apps are within healthy limits. Checks run automatically every refresh." });

            return list.OrderByDescending(f => f.Severity).ToList();
        }

        int HealthScore()
        {
            int score = 100;
            foreach (var f in findings)
                score -= f.Severity == Sev.Critical ? 25 : f.Severity == Sev.Warning ? 10 : f.Severity == Sev.Info ? 3 : 0;
            return Math.Max(0, score);
        }

        void UpdateHealth()
        {
            int score = HealthScore();
            int problems = findings.Count(f => f.Severity >= Sev.Warning);
            int tips = findings.Count(f => f.Severity == Sev.Info);
            nav[1].Badge = problems;
            nav[1].Invalidate();
            lblSideHealth.Text = "Health " + score + "/100\n" + (problems == 0 ? "No problems" : problems + " problem" + (problems == 1 ? "" : "s"));
            lblHealth.Text = score.ToString();
            lblHealth.ForeColor = score >= 80 ? T.Green : score >= 55 ? T.Orange : T.Red;
            lblHealthTitle.Text = score >= 80 ? "System health: good" : score >= 55 ? "System health: needs attention" : "System health: poor";
            lblHealthSub.Text = string.Format("{0} problem(s) · {1} tip(s) · checked {2:HH:mm:ss}", problems, tips, DateTime.Now);
        }

        void RenderCards(bool force)
        {
            var existing = cards.Controls.OfType<Card>().ToList();
            bool same = !force && existing.Count == findings.Count &&
                        existing.Select(c => c.Finding.Id + c.Finding.Severity).SequenceEqual(findings.Select(f => f.Id + f.Severity));
            if (same)
            {
                for (int i = 0; i < findings.Count; i++) existing[i].SetTexts(findings[i]);
                LayoutCards();
                return;
            }
            cards.SuspendLayout();
            foreach (var c in existing) { cards.Controls.Remove(c); c.Dispose(); }
            foreach (var f in findings)
            {
                var card = new Card(f, RunFinding, AskAboutFinding);
                cards.Controls.Add(card);
                Style(card);
                StyleTree(card);
            }
            LayoutCards();
            cards.ResumeLayout();
        }

        void LayoutCards()
        {
            int w = cards.Width - SystemInformation.VerticalScrollBarWidth - 6;   // always leave room for the scrollbar
            if (w < 200) return;
            foreach (var c in cards.Controls.OfType<Card>()) c.LayoutFor(w);
        }

        void RunFinding(Finding f)
        {
            try { if (f.Act != null) f.Act(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void AskAboutFinding(Finding f)
        {
            string q = "My Smart Advisor says: \"" + f.Title + "\" – " + f.Detail + " What exactly should I do, step by step, for my setup?";
            ShowPage(2);
            txtQ.Text = q;
            AskAi(q);
        }

        // Auto-troubleshoot actions (each is opt-in)
        void AutoActions()
        {
            int self;
            using (var me = Process.GetCurrentProcess()) self = me.Id;
            if (chkBoost.Checked)
            {
                int n = 0;
                foreach (var r in lastRows.Where(x => x.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase) && x.Priority == "Normal" && !boosted.Contains(x.Pid)))
                {
                    boosted.Add(r.Pid);
                    try { using (var p = Process.GetProcessById(r.Pid)) { p.PriorityClass = ProcessPriorityClass.AboveNormal; n++; } } catch { }
                }
                if (n > 0) Log(string.Format("Raised {0} Chrome process(es) to Above normal priority so Quantman stays smooth.", n));
            }

            var next = new Dictionary<int, int>();
            if (chkAutoLower.Checked)
            {
                int fg = Native.ForegroundPid();
                foreach (var r in lastRows)
                {
                    if (!r.CpuKnown || r.Cpu < 30) continue;
                    int c;
                    hogCount.TryGetValue(r.Pid, out c);
                    next[r.Pid] = ++c;
                    if (c < 3 || r.Priority != "Normal" || lowered.Contains(r.Pid) || r.Pid == fg || r.Pid == self ||
                        Protected.Contains(r.Name) || r.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase)) continue;
                    lowered.Add(r.Pid);
                    try
                    {
                        using (var p = Process.GetProcessById(r.Pid)) p.PriorityClass = ProcessPriorityClass.BelowNormal;
                        Log(string.Format("{0} (PID {1}) used {2:0}% CPU for {3}+ s in the background, so its priority was lowered to Below normal. Undo: right-click it → Set priority → Normal.",
                            r.Name, r.Pid, r.Cpu, 3 * Math.Max(1, IntervalSeconds)));
                    }
                    catch (Exception ex) { Log(string.Format("Couldn't lower {0} (PID {1}): {2}", r.Name, r.Pid, ex.Message)); }
                }
            }
            hogCount = next;

            if (chkAutoTrim.Checked && !trimming && lastSys != null && lastSys.MemPct >= 85 && (DateTime.Now - lastAutoTrim).TotalMinutes >= 10)
            {
                lastAutoTrim = DateTime.Now;
                Log(string.Format("RAM was {0:0}% used, so background apps were trimmed automatically.", lastSys.MemPct));
                TrimBackground();
            }
        }

        void Notify()
        {
            if (!chkNotify.Checked || ActiveForm == this) return;
            foreach (var f in findings.Where(x => x.Severity >= Sev.Warning))
            {
                DateTime last;
                if (notified.TryGetValue(f.Id, out last) && (DateTime.Now - last).TotalMinutes < 30) continue;
                notified[f.Id] = DateTime.Now;
                string detail = f.Detail.Length > 200 ? f.Detail.Substring(0, 197) + "…" : f.Detail;
                tray.ShowBalloonTip(6000, f.Title, detail, f.Severity == Sev.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning);
                break;
            }
        }

        void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            logLines.Add(line);
            if (logLines.Count > 200) logLines.RemoveAt(0);
            logBox.AppendText((logBox.TextLength > 0 ? Environment.NewLine : "") + line);
            try { File.AppendAllText(Path.Combine(dataDir, "troubleshoot.log"), DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine); } catch { }
        }

        // =============================================================== Ask AI (Claude Code)
        static string ClaudePath()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe");
            if (File.Exists(p)) return p;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (dir.Length == 0) continue;
                    string c = Path.Combine(dir.Trim(), "claude.exe");
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        string BuildReport()
        {
            var s = lastSys ?? new SysInfo();
            var sb = new StringBuilder();
            sb.AppendFormat("Time: {0:yyyy-MM-dd HH:mm}, PC uptime {1}\n", DateTime.Now, Uptime());
            sb.AppendFormat("CPU: {0:0}% now ({1} logical processors). Recent samples: {2}\n", Math.Max(0, s.Cpu), Environment.ProcessorCount,
                string.Join(", ", cpuHist.Select(v => v.ToString("0") + "%")));
            sb.AppendFormat("RAM: {0:0.0} of {1:0.0} GB used ({2:0}%), {3:0.0} GB available\n", Gb(s.MemTotal - s.MemAvail), Gb(s.MemTotal), s.MemPct, Gb(s.MemAvail));
            sb.AppendFormat("Committed memory: {0:0.0} of {1:0.0} GB ({2:0}%); page file about {3:0.0} GB; file cache {4:0.0} GB\n",
                Gb(s.CommitTotal), Gb(s.CommitLimit), s.CommitPct, Gb(s.PageFile), Gb(s.Cache));
            try { var d = new DriveInfo("C"); sb.AppendFormat("Drive C: {0:0.0} GB free of {1:0.0} GB\n", Gb(d.AvailableFreeSpace), Gb(d.TotalSize)); } catch { }
            sb.AppendFormat("Processes {0}, threads {1}, handles {2}\n", s.Processes, s.Threads, s.Handles);
            var groups = Group(lastRows);
            sb.Append("\nApps grouped by name, biggest committed memory first (name | processes | memory MB | commit MB | CPU % | priority):\n");
            foreach (var g in groups.OrderByDescending(g => g.Private).Take(18))
                sb.AppendFormat("- {0} | {1} | {2:0} | {3:0} | {4:0.0} | {5}\n", g.Name, g.Count, g.WorkingSet / 1048576.0, g.Private / 1048576.0, g.CpuKnown ? g.Cpu : 0, g.Priority);
            sb.Append("\nTop processes by CPU right now:\n");
            foreach (var r in lastRows.Where(r => r.CpuKnown).OrderByDescending(r => r.Cpu).Take(10))
                sb.AppendFormat("- {0} (PID {1}): {2:0.0}% CPU, {3:0} MB memory\n", r.Name, r.Pid, r.Cpu, r.WorkingSet / 1048576.0);
            sb.Append("\nSmart Advisor findings:\n");
            foreach (var f in findings) sb.AppendFormat("- [{0}] {1}: {2}\n", f.Severity, f.Title, f.Detail);
            if (logLines.Count > 0)
            {
                sb.Append("\nRecent actions taken by the app:\n");
                foreach (var l in logLines.Skip(Math.Max(0, logLines.Count - 8))) sb.Append("- ").Append(l).Append('\n');
            }
            return sb.ToString();
        }

        void ShowAiWelcome()
        {
            lastAnswer = null;
            RenderMarkdown("## Ask Claude about your PC\n" +
                "Pick a question above or type your own, then press **Enter**.\n\n" +
                "- Claude gets a snapshot of CPU, memory, your top apps and the Smart Advisor findings.\n" +
                "- Answers usually take 10–30 seconds. Claude Code runs only while it answers.\n" +
                "- Tip: double-click any process, or press **Ask AI about this** on an advisor card.");
            lastAnswer = null;
        }

        void AskAi(string question)
        {
            question = (question ?? "").Trim();
            if (question.Length == 0 || aiProc != null) return;
            if (lastSys == null) DoRefresh();
            string exe = ClaudePath();
            if (exe == null)
            {
                RenderMarkdown("## Claude Code was not found\nInstall Claude Code to get answers here, or use **Copy report** and paste it into claude.ai or ChatGPT.");
                return;
            }
            string aiDir = Path.Combine(dataDir, "ai");
            try { Directory.CreateDirectory(aiDir); } catch { }
            string prompt = AiContext + "\n\n=== SYSTEM SNAPSHOT ===\n" + BuildReport() + "\n=== QUESTION ===\n" + question + "\n";
            string model = Models[Math.Max(0, Math.Min(Models.Length - 1, cbModel.SelectedIndex))];
            var psi = new ProcessStartInfo(exe,
                "-p --disallowedTools \"" + DeniedTools + "\" --setting-sources project --strict-mcp-config --no-session-persistence " +
                "--append-system-prompt \"Answer directly from the snapshot in the prompt. Do not call any tools.\" --model " + model)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = aiDir,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
            p.Exited += (s, e) =>
            {
                try { p.WaitForExit(); } catch { }
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => AiDone(p))); } catch { }
            };
            try { p.Start(); }
            catch (Exception ex) { RenderMarkdown("## Couldn't start Claude Code\n" + ex.Message); return; }
            aiProc = p; aiOut = outSb; aiErr = errSb; aiStart = DateTime.Now; lastQuestion = question;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(prompt);
                p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                p.StandardInput.BaseStream.Flush();
                p.StandardInput.Close();
            }
            catch { }
            btnAsk.Enabled = false; btnCancel.Enabled = true;
            lblAi.Text = "Asking Claude (" + ModelNames[Math.Max(0, cbModel.SelectedIndex)] + ")…";
            RenderMarkdown("## Thinking…\n**Question:** " + question);
            lastAnswer = null;
            aiTimer.Start();
        }

        void AiTick()
        {
            if (aiProc == null) { aiTimer.Stop(); return; }
            int secs = (int)(DateTime.Now - aiStart).TotalSeconds;
            lblAi.Text = "Asking Claude (" + ModelNames[Math.Max(0, cbModel.SelectedIndex)] + ")… " + secs + " s";
            if (secs >= 180)
            {
                CancelAi(false);
                RenderMarkdown("## No answer after 3 minutes\nClaude Code didn't reply in time. Try again, pick the Fast model in Settings, or use **Copy report**.");
                lblAi.Text = "Timed out.";
            }
        }

        void AiDone(Process p)
        {
            if (p != aiProc) { try { p.Dispose(); } catch { } return; }   // cancelled
            aiTimer.Stop();
            aiProc = null;
            btnAsk.Enabled = true; btnCancel.Enabled = false;
            int secs = (int)(DateTime.Now - aiStart).TotalSeconds;
            string text, err;
            lock (aiOut) text = aiOut.ToString().Trim();
            lock (aiErr) err = aiErr.ToString().Trim();
            int code = -1;
            try { code = p.ExitCode; } catch { }
            p.Dispose();
            if (text.Length == 0)
            {
                RenderMarkdown("## Claude Code returned no answer\n" + (err.Length > 0 ? "`" + (err.Length > 600 ? err.Substring(0, 600) : err).Replace("`", "'") + "`\n\n" : "") +
                    "If you haven't signed in yet, open a terminal, run **claude** once and sign in, then try again.");
                lblAi.Text = "No answer (exit code " + code + ").";
                return;
            }
            string md = "**Question:** " + lastQuestion + "\n\n" + text;
            RenderMarkdown(md);
            lastAnswer = md;
            lblAi.Text = string.Format("Answered in {0} s · {1} · {2:HH:mm}", secs, ModelNames[Math.Max(0, cbModel.SelectedIndex)], DateTime.Now);
            try { File.WriteAllText(Path.Combine(dataDir, @"ai\last-answer.md"), md, Encoding.UTF8); } catch { }
            if (ActiveForm != this) tray.ShowBalloonTip(4000, "Claude answered", lastQuestion.Length > 120 ? lastQuestion.Substring(0, 117) + "…" : lastQuestion, ToolTipIcon.Info);
        }

        void CancelAi(bool show)
        {
            var p = aiProc;
            aiProc = null;
            aiTimer.Stop();
            if (p != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false });
                }
                catch { }
            }
            if (show)
            {
                btnAsk.Enabled = true; btnCancel.Enabled = false;
                lblAi.Text = "Cancelled.";
            }
        }

        void RenderMarkdown(string md)
        {
            rtb.Clear();
            rtb.BackColor = T.Surface;
            Font bodyFont = F.Md, boldFont = F.MdBold, h = F.MdHead;
            bool inCode = false;
            foreach (var raw in md.Replace("\r", "").Split('\n'))
            {
                string line = raw;
                if (line.TrimStart().StartsWith("```")) { inCode = !inCode; continue; }
                if (inCode) { Append(line + "\n", F.Mono, T.Sub); continue; }
                if (line.StartsWith("#"))
                {
                    Append(line.TrimStart('#', ' ') + "\n", h, T.Text);
                    continue;
                }
                var m = Regex.Match(line, @"^(\s*)([-*+]|\d+\.)\s+(.*)$");
                if (m.Success)
                {
                    string bullet = m.Groups[2].Value.EndsWith(".") ? m.Groups[2].Value : "•";
                    Append(new string(' ', m.Groups[1].Value.Length * 2) + "  " + bullet + "  ", bodyFont, T.Accent);
                    line = m.Groups[3].Value;
                }
                foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*|`[^`]+`)"))
                {
                    if (part.Length == 0) continue;
                    if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4) Append(part.Substring(2, part.Length - 4), boldFont, T.Text);
                    else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2) Append(part.Substring(1, part.Length - 2), F.Mono, T.Accent);
                    else Append(part.Replace("**", ""), bodyFont, T.Text);
                }
                Append("\n", bodyFont, T.Text);
            }
            rtb.SelectionStart = 0;
            rtb.ScrollToCaret();
        }

        void Append(string text, Font font, Color color)
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionFont = font;
            rtb.SelectionColor = color;
            rtb.AppendText(text);
        }

        // =============================================================== Self-update from the source folder
        void UpdateUpdateLabel()
        {
            if (updating) return;
            string s = "Installed: v" + AppInfo.Version + ", built " + File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm") + ".";
            if (pendingVersion != null) s += "  Update ready: v" + pendingVersion + ".";
            else if (File.Exists(srcFile)) s += "  The source folder has no newer version.";
            else s += "  Source folder not found, so updates are off.";
            if (lastUpdateError != null) s += "\nLast update failed: " + lastUpdateError;
            lblUpdate.Text = s + "\nUpdates are checked every 30 minutes from: " + srcFile;
        }

        void CheckForUpdate(bool userInitiated)
        {
            if (updating) return;
            try
            {
                if (!File.Exists(srcFile)) { pendingVersion = null; UpdateUpdateLabel(); return; }
                string text = File.ReadAllText(srcFile);
                var m = Regex.Match(text, "const string Version = \"(\\d+\\.\\d+\\.\\d+)\"");
                var cur = new Version(AppInfo.Version);
                Version src = m.Success ? new Version(m.Groups[1].Value) : null;
                bool newer = src != null && src > cur;
                bool changed = File.GetLastWriteTimeUtc(srcFile) > File.GetLastWriteTimeUtc(Application.ExecutablePath).AddSeconds(5);
                if (newer || changed)
                {
                    pendingVersion = src != null && newer ? src.ToString() : AppInfo.Version + " (source changed)";
                    bannerText.Text = "  Update ready: v" + pendingVersion + " — built from your source folder.";
                    bannerBtn.Text = "Install update";
                    banner.Visible = true;
                    if (chkAutoUpdate.Checked && aiProc == null && !userInitiated) InstallUpdate();
                }
                else
                {
                    pendingVersion = null;
                    banner.Visible = false;
                    if (userInitiated) MessageBox.Show(this, "You're on the latest version (v" + AppInfo.Version + ").", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { lastUpdateError = ex.Message; }
            UpdateUpdateLabel();
        }

        void InstallUpdate()
        {
            if (updating) return;
            string csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            if (!File.Exists(csc) || !File.Exists(srcFile))
            {
                lastUpdateError = "The C# compiler or the source file is missing.";
                UpdateUpdateLabel();
                return;
            }
            updating = true;
            banner.Visible = true;
            bannerText.Text = "  Building the update…";
            bannerBtn.Enabled = false;
            lblUpdate.Text = "Building the update…";
            string newExe = Path.Combine(baseDir, "TaskManagerLite.new.exe");
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
            string ico = Path.Combine(baseDir, @"src\TaskManagerLite.ico");
            var psi = new ProcessStartInfo(csc,
                "/nologo /target:winexe /platform:x64 /optimize+ " + (File.Exists(ico) ? "/win32icon:\"" + ico + "\" " : "") +
                "/out:\"" + newExe + "\" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll \"" + srcFile + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = baseDir
            };
            var output = new StringBuilder();
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            p.Exited += (s, e) =>
            {
                try { p.WaitForExit(); } catch { }
                int code = -1;
                try { code = p.ExitCode; } catch { }
                string text;
                lock (output) text = output.ToString();
                try { if (!IsDisposed) BeginInvoke((Action)(() => FinishUpdate(code, text, newExe))); } catch { }
            };
            try { p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
            catch (Exception ex) { updating = false; bannerBtn.Enabled = true; lastUpdateError = ex.Message; UpdateUpdateLabel(); }
        }

        void FinishUpdate(int code, string output, string newExe)
        {
            try { File.WriteAllText(Path.Combine(dataDir, "update.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  exit " + code + Environment.NewLine + output); } catch { }
            if (code != 0 || !File.Exists(newExe))
            {
                updating = false;
                bannerBtn.Enabled = true;
                string first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("error")) ?? "Build failed (see data\\update.log).";
                lastUpdateError = first.Length > 300 ? first.Substring(0, 300) : first;
                bannerText.Text = "  The update failed to build. The current version keeps running.";
                Log("Update build failed: " + lastUpdateError);
                UpdateUpdateLabel();
                return;
            }
            string exe = Application.ExecutablePath;
            string old = Path.Combine(baseDir, "TaskManagerLite.old.exe");
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);          // a running .exe can be renamed, not deleted
                File.Move(newExe, exe);
            }
            catch (Exception ex)
            {
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                updating = false;
                bannerBtn.Enabled = true;
                lastUpdateError = "Couldn't replace the program file: " + ex.Message;
                UpdateUpdateLabel();
                return;
            }
            Log("Update built. Restarting…");
            SaveSettings();
            int pid;
            using (var me = Process.GetCurrentProcess()) pid = me.Id;
            try { Process.Start(exe, "--wait " + pid + " --updated"); } catch { }
            exiting = true;
            Close();
        }

        // =============================================================== Settings
        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("TaskManagerLite", "\"" + Application.ExecutablePath + "\" --minimized");
                    else if (k.GetValue("TaskManagerLite") != null) k.DeleteValue("TaskManagerLite", false);
                }
            }
            catch { }
        }

        void LoadSettings()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(settingsPath))
                    foreach (var line in File.ReadAllLines(settingsPath))
                    {
                        var kv = line.Split(new[] { '=' }, 2);
                        if (kv.Length == 2) d[kv[0].Trim()] = kv[1].Trim();
                    }
            }
            catch { }
            Func<string, int, int> num = (k, def) => { string v; int n; return d.TryGetValue(k, out v) && int.TryParse(v, out n) ? n : def; };
            Func<string, bool, bool> flag = (k, def) => { string v; return d.TryGetValue(k, out v) ? v == "1" : def; };

            int iv = num("interval", 0);
            cbInterval.SelectedIndex = iv >= 0 && iv < IntervalSecs.Length ? iv : 0;
            chkGroup.Checked = flag("group", false);
            chkTop.Checked = flag("topmost", false);
            TopMost = chkTop.Checked;
            int sc = num("sort", 2);
            sortCol = sc >= 0 && sc < ColumnNames.Length ? sc : 2;
            sortDesc = flag("desc", true);
            themeIdx = Math.Max(0, Math.Min(2, num("theme", 0)));
            cbTheme.SelectedIndex = themeIdx;
            cbModel.SelectedIndex = Math.Max(0, Math.Min(Models.Length - 1, num("model", 1)));
            chkTray.Checked = flag("tray", false);
            chkNotify.Checked = flag("notify", true);
            chkAutoLower.Checked = flag("autolower", false);
            chkBoost.Checked = flag("boostchrome", false);
            chkAutoUpdate.Checked = flag("autoupdate", true);
            chkAutoTrim.Checked = flag("autotrim", false);
            chkTrimChrome.Checked = flag("trimchrome", false);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    chkStartup.Checked = k != null && k.GetValue("TaskManagerLite") != null;
            }
            catch { }
            var area = Screen.PrimaryScreen.WorkingArea;
            int w = num("width", 0), h = num("height", 0);
            if (w >= MinimumSize.Width) Width = Math.Min(w, area.Width);
            if (h >= MinimumSize.Height) Height = Math.Min(h, area.Height);
        }

        void SaveSettings()
        {
            if (loading) return;
            try
            {
                var lines = new List<string> {
                    "interval=" + Math.Max(0, cbInterval.SelectedIndex),
                    "group=" + (chkGroup.Checked ? "1" : "0"),
                    "topmost=" + (chkTop.Checked ? "1" : "0"),
                    "sort=" + sortCol,
                    "desc=" + (sortDesc ? "1" : "0"),
                    "theme=" + themeIdx,
                    "model=" + Math.Max(0, cbModel.SelectedIndex),
                    "tray=" + (chkTray.Checked ? "1" : "0"),
                    "notify=" + (chkNotify.Checked ? "1" : "0"),
                    "autolower=" + (chkAutoLower.Checked ? "1" : "0"),
                    "boostchrome=" + (chkBoost.Checked ? "1" : "0"),
                    "autoupdate=" + (chkAutoUpdate.Checked ? "1" : "0"),
                    "autotrim=" + (chkAutoTrim.Checked ? "1" : "0"),
                    "trimchrome=" + (chkTrimChrome.Checked ? "1" : "0") };
                if (WindowState == FormWindowState.Normal && Visible) { lines.Add("width=" + Width); lines.Add("height=" + Height); }
                File.WriteAllLines(settingsPath, lines.ToArray());
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            int waitPid = -1, startPage = 0;
            bool minimized = false, updated = false;
            string ask = null;
            bool purge = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--wait" && i + 1 < args.Length) int.TryParse(args[++i], out waitPid);
                else if (args[i] == "--page" && i + 1 < args.Length) int.TryParse(args[++i], out startPage);
                else if (args[i] == "--ask" && i + 1 < args.Length) ask = args[++i];
                else if (args[i] == "--purge") purge = true;
                else if (args[i] == "--minimized") minimized = true;
                else if (args[i] == "--updated") updated = true;
            }
            if (waitPid > 0)
            {
                try { using (var p = Process.GetProcessById(waitPid)) p.WaitForExit(15000); } catch { }
            }
            try
            {
                string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TaskManagerLite.old.exe");
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }

            bool first;
            using (var mutex = new Mutex(true, @"Local\TaskManagerLite.VK", out first))
            {
                if (!first)
                {
                    // Already running: bring that window to the front instead of opening a second copy.
                    using (var me = Process.GetCurrentProcess())
                        foreach (var p in Process.GetProcessesByName(me.ProcessName))
                            using (p)
                                if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero)
                                {
                                    Native.ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                                    Native.SetForegroundWindow(p.MainWindowHandle);
                                }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                T.Set(false);
                Application.Run(new MainForm(minimized, updated) { StartPage = startPage, StartQuestion = ask, StartPurge = purge });
            }
        }
    }
}
