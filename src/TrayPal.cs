// TrayPal - a tiny always-on-top desktop companion for Windows: network speed, CPU, RAM, disk, battery, brightness, dark mode.
// https://mrobaidi.com  |  MIT licence
// Build: build.cmd (uses the C# compiler that ships with Windows .NET Framework 4.x). No dependencies.
// Written in C# 5 syntax so the built-in csc.exe can compile it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

[assembly: AssemblyTitle("TrayPal")]
[assembly: AssemblyProduct("TrayPal")]
[assembly: AssemblyDescription("Tiny always-on-top desktop companion")]
[assembly: AssemblyCompany("mrobaidi.com")]
[assembly: AssemblyCopyright("MIT licence")]
[assembly: AssemblyVersion(AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(AppInfo.Version + ".0")]

static class AppInfo
{
    public const string Name = "TrayPal";
    public const string Version = "1.0.0";
    public const string Website = "https://mrobaidi.com";
    public const string Repo = "https://github.com/ahmedco88/traypal";
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new System.Threading.Mutex(true, "TrayPal_SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new MeterForm());
        }
    }
}

class MeterForm : Form
{
    const string RegKey = @"Software\TrayPal";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int HistoryLen = 120;          // seconds of graph
    const int HeaderH = 34;

    readonly Timer timer = new Timer();
    readonly List<double> downHist = new List<double>();
    readonly List<double> upHist = new List<double>();
    readonly NotifyIcon tray = new NotifyIcon();
    ContextMenuStrip menu;

    string adapterId = "";              // "" = auto (adapter with default gateway)
    long lastRx = -1, lastTx = -1;
    DateTime lastT;
    double down, up;                     // bytes/sec
    bool clickThrough;
    bool useBits;
    bool onTop = true;

    // CPU + RAM panel
    bool showSys;
    Size baseSize = new Size(240, 110);   // size of the network part
    readonly List<double> cpuHist = new List<double>();
    readonly List<double> ramHist = new List<double>();
    double cpu, ram;
    // The counter is built on a background thread (first-time setup takes seconds) and handed over through this field.
    volatile System.Diagnostics.PerformanceCounter cpuCounter;
    volatile int cpuState;                      // 0 = not started, 1 = starting, 2 = failed (retried when the panel is toggled on)

    // Optional disk + battery line (off by default); text is rebuilt once a minute, not every tick.
    bool showInfo;
    const int InfoH = 18;
    string infoText;                      // null = rebuild on the next tick
    DateTime infoAt;

    // Adapter cache: the chosen adapter is reused until a network event, a menu pick or a failed read says otherwise.
    NetworkInterface cachedNi;
    volatile bool repick = true;          // set from network-change events (other threads), so it only ever sets a flag

    // Fonts are created once, not on every paint.
    readonly Font fHead = new Font("Segoe UI Semibold", 10f);
    readonly Font fName = new Font("Segoe UI", 7.5f);
    readonly Font fAxis = new Font("Segoe UI", 7f);
    readonly Font fSys = new Font("Segoe UI Semibold", 9f);

    // ---------- theme ----------
    int themeMode;   // 0 = dark, 1 = light, 2 = match Windows
    bool light;
    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    Color CpuCol  { get { return light ? C(25, 105, 215) : C(90, 165, 255); } }
    Color RamCol  { get { return light ? C(140, 60, 210) : C(200, 125, 255); } }
    Color DownCol { get { return light ? C(20, 140, 70)  : C(80, 200, 120); } }
    Color UpCol   { get { return light ? C(215, 110, 10) : C(255, 160, 60); } }
    Color WinBg   { get { return light ? C(246, 247, 250) : C(20, 22, 28); } }
    Color PanelBg { get { return light ? C(232, 235, 241) : C(30, 33, 42); } }
    Color GridCol { get { return light ? C(212, 216, 225) : C(50, 55, 68); } }
    Color SubText { get { return light ? C(95, 105, 120)  : C(140, 150, 165); } }
    Color AxisText{ get { return light ? C(110, 118, 132) : C(120, 130, 145); } }

    void ApplyTheme()
    {
        light = themeMode == 1 || (themeMode == 2 && WinTheme.IsLight());
        BackColor = WinBg;
        if (themeMenu != null)
            for (int i = 0; i < themeMenu.DropDownItems.Count; i++)
                ((ToolStripMenuItem)themeMenu.DropDownItems[i]).Checked = i == themeMode;
        Invalidate();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);


    public MeterForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Text = AppInfo.Name;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        LoadSettings();
        TopMost = onTop;
        ApplyLayout();
        BuildMenu();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnWinPrefChanged;
        NetworkChange.NetworkAddressChanged += OnNetChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetAvailChanged;

        tray.Icon = Icon ?? SystemIcons.Information;
        tray.Text = AppInfo.Name;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate { SetClickThrough(false); Show(); BringToFront(); Activate(); };
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowBrightness(); };

        ContextMenuStrip = menu;
        MouseDown += OnDrag;

        timer.Interval = 1000;
        timer.Tick += delegate { Sample(); Invalidate(); };
        timer.Start();
        Sample();
    }

    // ---------- sampling ----------
    NetworkInterface PickAdapter()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        if (adapterId != "")
        {
            var n = all.FirstOrDefault(a => a.Id == adapterId);
            if (n != null) return n;
        }
        // auto: up, not loopback/tunnel, has a default gateway; prefer most traffic
        return all.Where(a => a.OperationalStatus == OperationalStatus.Up
                    && a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                    && a.GetIPProperties().GatewayAddresses.Any(g => !g.Address.ToString().StartsWith("0.")))
                  .OrderByDescending(a => a.GetIPStatistics().BytesReceived)
                  .FirstOrDefault();
    }

    // These fire on a thread-pool thread: set the flag and nothing else.
    void OnNetChanged(object s, EventArgs e) { repick = true; }
    void OnNetAvailChanged(object s, NetworkAvailabilityEventArgs e) { repick = true; }

    IPInterfaceStatistics TryStats(NetworkInterface n, bool requireUp)
    {
        if (n == null) return null;
        try
        {
            if (requireUp && n.OperationalStatus != OperationalStatus.Up) return null;
            return n.GetIPStatistics();
        }
        catch { return null; }
    }

    // Returns the adapter to read and its fresh counters. Re-enumerates adapters only when needed.
    IPInterfaceStatistics ReadStats(out NetworkInterface ni)
    {
        bool fresh = repick || cachedNi == null;
        if (fresh)
        {
            repick = false;
            try { cachedNi = PickAdapter(); } catch { cachedNi = null; }
        }
        ni = cachedNi;
        // The Up check only matters for a reused adapter; a just-picked one is taken as it is.
        var s = TryStats(ni, !fresh);
        if (s == null && !fresh)
        {
            try { cachedNi = PickAdapter(); } catch { cachedNi = null; }
            ni = cachedNi;
            s = TryStats(ni, false);
        }
        return s;
    }

    string currentName = "";

    int SysPanelH { get { return (int)(baseSize.Height * 0.8); } }

    void ApplyLayout()
    {
        Size = new Size(baseSize.Width, baseSize.Height + (showSys ? SysPanelH : 0) + (showInfo ? InfoH : 0));
        // keep the window fully on screen when it grows
        var wa = Screen.FromPoint(Location).WorkingArea;
        int x = Math.Min(Math.Max(Left, wa.Left), wa.Right - Width);
        int y = Math.Min(Math.Max(Top, wa.Top), wa.Bottom - Height);
        Location = new Point(x, y);
        Invalidate();
    }

    void OnWinPrefChanged(object s, UserPreferenceChangedEventArgs e)
    {
        if (themeMode == 2) BeginInvoke((MethodInvoker)ApplyTheme);
    }

    // Runs on a background thread. The counter is only published after its first NextValue(), so the UI
    // thread never waits on it and its first reading covers the time since this one.
    void BuildCpuCounter()
    {
        try
        {
            System.Diagnostics.PerformanceCounter c;
            // "% Processor Utility" matches Task Manager; fall back for older Windows
            try { c = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); c.NextValue(); }
            catch { c = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total"); c.NextValue(); }
            cpuCounter = c;
        }
        catch { cpuState = 2; }
    }

    void SampleSys()
    {
        if (!showSys) return;
        var cc = cpuCounter;
        if (cc != null)
        {
            try { cpu = Math.Min(100, Math.Max(0, cc.NextValue())); }
            catch { cpu = 0; }
        }
        else if (cpuState == 0)
        {
            cpuState = 1;
            var th = new System.Threading.Thread(BuildCpuCounter);
            th.IsBackground = true;
            th.Start();
        }

        var m = new MEMORYSTATUSEX();
        m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
            ram = 100.0 * (m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys;

        cpuHist.Add(cpu); ramHist.Add(ram);
        if (cpuHist.Count > HistoryLen) { cpuHist.RemoveAt(0); ramHist.RemoveAt(0); }
    }

    static string FmtSize(long bytes)
    {
        double gb = bytes / 1073741824.0;
        if (gb >= 1024) return (gb / 1024).ToString("0.0") + "T";
        return gb.ToString(gb >= 100 ? "0" : "0.0") + "G";
    }

    void RefreshInfo()
    {
        infoAt = DateTime.UtcNow;
        var parts = new List<string>();
        try
        {
            var drives = new List<string>();
            foreach (var d in System.IO.DriveInfo.GetDrives())
                if (d.DriveType == System.IO.DriveType.Fixed && d.IsReady)
                    drives.Add(d.Name.Substring(0, 1) + " " + FmtSize(d.AvailableFreeSpace));
            if (drives.Count > 0) parts.Add("Free  " + string.Join("  ", drives.ToArray()));
        }
        catch { }
        try
        {
            var ps = SystemInformation.PowerStatus;
            var st = ps.BatteryChargeStatus;
            float pct = ps.BatteryLifePercent;
            // No battery (desktop) or unknown state: the battery part is left out entirely.
            if ((st & BatteryChargeStatus.NoSystemBattery) == 0 && st != BatteryChargeStatus.Unknown && pct >= 0f && pct <= 1f)
                parts.Add("Battery " + (int)Math.Round(pct * 100) + "%" + (ps.PowerLineStatus == PowerLineStatus.Online ? " (plugged in)" : ""));
        }
        catch { }
        infoText = string.Join("   ", parts.ToArray());
    }

    void Sample()
    {
        if (showInfo && (infoText == null || (DateTime.UtcNow - infoAt).TotalSeconds >= 60)) RefreshInfo();
        SampleSys();
        NetworkInterface ni;
        var s = ReadStats(out ni);
        if (ni == null || s == null) { currentName = "No adapter"; lastRx = -1; Push(0, 0); return; }
        if (ni.Name != currentName) { currentName = ni.Name; lastRx = -1; }

        long rx = s.BytesReceived, tx = s.BytesSent;
        var now = DateTime.UtcNow;
        if (lastRx >= 0)
        {
            double dt = (now - lastT).TotalSeconds;
            if (dt <= 0) dt = 1;
            down = Math.Max(0, (rx - lastRx) / dt);
            up = Math.Max(0, (tx - lastTx) / dt);
            Push(down, up);
        }
        lastRx = rx; lastTx = tx; lastT = now;
        tray.Text = Trunc(AppInfo.Name + "  ↓" + Fmt(down) + "  ↑" + Fmt(up), 63);
    }

    void Push(double d, double u)
    {
        downHist.Add(d); upHist.Add(u);
        if (downHist.Count > HistoryLen) { downHist.RemoveAt(0); upHist.RemoveAt(0); }
    }

    static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

    string Fmt(double bps)
    {
        if (useBits)
        {
            double b = bps * 8;
            if (b >= 1e9) return (b / 1e9).ToString("0.00") + " Gb/s";
            if (b >= 1e6) return (b / 1e6).ToString("0.0") + " Mb/s";
            return (b / 1e3).ToString("0") + " Kb/s";
        }
        if (bps >= 1048576) return (bps / 1048576).ToString("0.00") + " MB/s";
        return (bps / 1024).ToString("0") + " KB/s";
    }

    // ---------- drawing ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var dB = new SolidBrush(DownCol))
        using (var uB = new SolidBrush(UpCol))
        using (var grey = new SolidBrush(SubText))
        {
            g.DrawString("↓ " + Fmt(down), fHead, dB, 6, 2);
            g.DrawString("↑ " + Fmt(up), fHead, uB, Width / 2 + 2, 2);
            g.DrawString(Trunc(currentName, 40), fName, grey, 6, 19);
        }

        int netH = baseSize.Height;
        var area = new Rectangle(4, HeaderH, Width - 8, netH - HeaderH - 4);
        using (var bg = new SolidBrush(PanelBg))
            g.FillRectangle(bg, area);

        double max = 1;
        foreach (var v in downHist) if (v > max) max = v;
        foreach (var v in upHist) if (v > max) max = v;
        max = NiceCeil(max);

        using (var grid = new Pen(GridCol))
        {
            for (int i = 1; i < 4; i++)
            {
                int y = area.Bottom - area.Height * i / 4;
                g.DrawLine(grid, area.Left, y, area.Right, y);
            }
        }
        DrawSeries(g, downHist, area, max, DownCol, true);
        DrawSeries(g, upHist, area, max, UpCol, false);

        using (var grey = new SolidBrush(AxisText))
        {
            var lbl = Fmt(max);
            var sz = g.MeasureString(lbl, fAxis);
            g.DrawString(lbl, fAxis, grey, area.Right - sz.Width - 1, area.Top + 1);
        }

        if (showSys) PaintSys(g, netH);
        if (showInfo && infoText != null)
        {
            using (var grey = new SolidBrush(SubText))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(infoText, fName, grey, new RectangleF(6, Height - InfoH + 2, Width - 10, InfoH - 2), sf);
            }
        }
    }

    void PaintSys(Graphics g, int y0)
    {
        using (var cB = new SolidBrush(CpuCol))
        using (var rB = new SolidBrush(RamCol))
        {
            g.DrawString("CPU " + cpu.ToString("0") + "%", fSys, cB, 6, y0);
            g.DrawString("RAM " + ram.ToString("0") + "%", fSys, rB, Width / 2 + 2, y0);
        }
        const int head = 18;
        var area = new Rectangle(4, y0 + head, Width - 8, Height - (showInfo ? InfoH : 0) - y0 - head - 4);
        if (area.Height < 8) return;
        using (var bg = new SolidBrush(PanelBg))
            g.FillRectangle(bg, area);
        using (var grid = new Pen(GridCol))
            for (int i = 1; i < 4; i++)
            {
                int y = area.Bottom - area.Height * i / 4;
                g.DrawLine(grid, area.Left, y, area.Right, y);
            }
        DrawSeries(g, cpuHist, area, 100, CpuCol, true);
        DrawSeries(g, ramHist, area, 100, RamCol, false);
    }

    static double NiceCeil(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (v <= m * p) return m * p;
        return 10 * p;
    }

    static void DrawSeries(Graphics g, List<double> h, Rectangle a, double max, Color c, bool fill)
    {
        if (h.Count < 2) return;
        float step = (float)a.Width / (HistoryLen - 1);
        float x0 = a.Right - (h.Count - 1) * step;
        var pts = new PointF[h.Count];
        for (int i = 0; i < h.Count; i++)
            pts[i] = new PointF(x0 + i * step, (float)(a.Bottom - h[i] / max * (a.Height - 2)));
        if (fill)
        {
            var poly = new List<PointF>(pts);
            poly.Add(new PointF(pts[pts.Length - 1].X, a.Bottom));
            poly.Add(new PointF(pts[0].X, a.Bottom));
            using (var b = new SolidBrush(Color.FromArgb(70, c)))
                g.FillPolygon(b, poly.ToArray());
        }
        using (var p = new Pen(c, 1.5f))
            g.DrawLines(p, pts);
    }

    // ---------- dragging / click-through ----------
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW; return cp; } // hide from Alt+Tab
    }

    void OnDrag(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
        SaveSettings();
    }

    protected override void OnMove(EventArgs e) { base.OnMove(e); }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveSettings(); }

    void SetClickThrough(bool on)
    {
        clickThrough = on;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE) | WS_EX_LAYERED;
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
        if (clickThroughItem != null) clickThroughItem.Checked = on;
        SaveSettings();
        if (on) tray.ShowBalloonTip(3000, AppInfo.Name, "Click-through on. Right-click the tray icon to turn it off.", ToolTipIcon.Info);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (clickThrough) SetClickThrough(true);
    }

    // ---------- brightness popup ----------
    BrightnessPopup popup;
    DateTime popupClosed;

    void ShowBrightness()
    {
        // Clicking the tray icon while the card is open closes it (click-away) just before this runs,
        // so a click straight after a close is that same click: leave it closed.
        if ((popup != null && !popup.IsDisposed) || (DateTime.Now - popupClosed).TotalMilliseconds < 300) return;
        popup = new BrightnessPopup(Cursor.Position, light);
        popup.FormClosed += delegate { popupClosed = DateTime.Now; };
        popup.Show();
    }

    static string WinSwitchText() { return WinTheme.IsLight() ? "Switch Windows to dark mode" : "Switch Windows to light mode"; }

    // ---------- menu ----------
    ToolStripMenuItem themeMenu, clickThroughItem, adapterMenu, opacityMenu, sizeMenu, startupItem, bitsItem, winThemeItem;

    void BuildMenu()
    {
        menu = new ContextMenuStrip();

        var brightItem = new ToolStripMenuItem("Brightness...");
        brightItem.Click += delegate { ShowBrightness(); };
        menu.Items.Add(brightItem);

        winThemeItem = new ToolStripMenuItem(WinSwitchText());
        winThemeItem.Click += delegate { WinTheme.Toggle(); winThemeItem.Text = WinSwitchText(); };
        menu.Items.Add(winThemeItem);
        menu.Opening += delegate { winThemeItem.Text = WinSwitchText(); };   // someone may have flipped it elsewhere
        menu.Items.Add(new ToolStripSeparator());

        adapterMenu = new ToolStripMenuItem("Adapter");
        adapterMenu.DropDownOpening += delegate { FillAdapters(); };
        adapterMenu.DropDownItems.Add("(loading)");
        menu.Items.Add(adapterMenu);

        opacityMenu = new ToolStripMenuItem("Opacity");
        foreach (var o in new[] { 100, 85, 70, 55, 40 })
        {
            int val = o;
            var it = new ToolStripMenuItem(o + "%");
            it.Checked = Math.Abs(Opacity * 100 - o) < 1;
            it.Click += delegate
            {
                Opacity = val / 100.0;
                foreach (ToolStripMenuItem x in opacityMenu.DropDownItems) x.Checked = x == it;
                SaveSettings();
            };
            opacityMenu.DropDownItems.Add(it);
        }
        menu.Items.Add(opacityMenu);

        sizeMenu = new ToolStripMenuItem("Size");
        foreach (var sz in new[] { new Size(200, 90), new Size(240, 110), new Size(320, 150) })
        {
            var s2 = sz;
            var it = new ToolStripMenuItem(sz.Width + " x " + sz.Height);
            it.Checked = baseSize == sz;
            it.Click += delegate
            {
                baseSize = s2; ApplyLayout();
                foreach (ToolStripMenuItem x in sizeMenu.DropDownItems) x.Checked = x == it;
                SaveSettings(); Invalidate();
            };
            sizeMenu.DropDownItems.Add(it);
        }
        menu.Items.Add(sizeMenu);

        bitsItem = new ToolStripMenuItem("Show in Mb/s (bits)");
        bitsItem.Checked = useBits;
        bitsItem.Click += delegate { useBits = !useBits; bitsItem.Checked = useBits; SaveSettings(); Invalidate(); };
        menu.Items.Add(bitsItem);

        themeMenu = new ToolStripMenuItem("Theme");
        var themeNames = new[] { "Dark", "Light", "Match Windows" };
        for (int i = 0; i < themeNames.Length; i++)
        {
            int mode = i;
            var it = new ToolStripMenuItem(themeNames[i]);
            it.Click += delegate { themeMode = mode; ApplyTheme(); SaveSettings(); };
            themeMenu.DropDownItems.Add(it);
        }
        menu.Items.Add(themeMenu);

        var sysItem = new ToolStripMenuItem("Show CPU + RAM graph");
        sysItem.Checked = showSys;
        sysItem.Click += delegate
        {
            showSys = !showSys; sysItem.Checked = showSys;
            if (!showSys) { cpuHist.Clear(); ramHist.Clear(); }
            else if (cpuState == 2) cpuState = 0;   // retry a counter that failed earlier
            ApplyLayout(); SaveSettings();
        };
        menu.Items.Add(sysItem);

        var infoItem = new ToolStripMenuItem("Show disk + battery line");
        infoItem.Checked = showInfo;
        infoItem.Click += delegate
        {
            showInfo = !showInfo; infoItem.Checked = showInfo;
            infoText = null;
            if (showInfo) RefreshInfo();
            ApplyLayout(); SaveSettings();
        };
        menu.Items.Add(infoItem);

        var topItem = new ToolStripMenuItem("Always on top");
        topItem.Checked = onTop;
        topItem.Click += delegate { onTop = !onTop; TopMost = onTop; topItem.Checked = onTop; SaveSettings(); };
        menu.Items.Add(topItem);

        clickThroughItem = new ToolStripMenuItem("Click-through (ignore mouse)");
        clickThroughItem.Checked = clickThrough;
        clickThroughItem.Click += delegate { SetClickThrough(!clickThrough); };
        menu.Items.Add(clickThroughItem);

        startupItem = new ToolStripMenuItem("Start with Windows");
        startupItem.Checked = IsStartup();
        startupItem.Click += delegate { SetStartup(!IsStartup()); startupItem.Checked = IsStartup(); };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        var about = new ToolStripMenuItem("About " + AppInfo.Name + "...");
        about.Click += delegate { using (var a = new AboutForm(Icon)) a.ShowDialog(this); };
        menu.Items.Add(about);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += delegate { SaveSettings(); tray.Visible = false; Application.Exit(); };
        menu.Items.Add(exit);
    }

    void FillAdapters()
    {
        adapterMenu.DropDownItems.Clear();
        var auto = new ToolStripMenuItem("Auto (main connection)");
        auto.Checked = adapterId == "";
        auto.Click += delegate { adapterId = ""; lastRx = -1; repick = true; SaveSettings(); };
        adapterMenu.DropDownItems.Add(auto);
        adapterMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
                 .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback && a.OperationalStatus == OperationalStatus.Up))
        {
            string id = n.Id;
            var it = new ToolStripMenuItem(n.Name + "  (" + n.Description + ")");
            it.Checked = adapterId == id;
            it.Click += delegate { adapterId = id; lastRx = -1; repick = true; SaveSettings(); };
            adapterMenu.DropDownItems.Add(it);
        }
    }

    // ---------- settings ----------
    bool IsStartup()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("TrayPal") != null;
    }

    void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("TrayPal", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("TrayPal", false);
        }
    }

    void LoadSettings()
    {
        Opacity = 0.85;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - baseSize.Width - 12, wa.Bottom - baseSize.Height - 12);
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
        {
            if (k == null) return;
            try
            {
                int x = (int)k.GetValue("X", Location.X), y = (int)k.GetValue("Y", Location.Y);
                int w = (int)k.GetValue("W", baseSize.Width), h = (int)k.GetValue("H", baseSize.Height);
                baseSize = new Size(w, h);
                var p = new Point(x, y);
                if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(p))) Location = p;
                Opacity = (int)k.GetValue("Opacity", 85) / 100.0;
                adapterId = (string)k.GetValue("Adapter", "");
                clickThrough = (int)k.GetValue("ClickThrough", 0) == 1;
                useBits = (int)k.GetValue("Bits", 0) == 1;
                showSys = (int)k.GetValue("Sys", 0) == 1;
                showInfo = (int)k.GetValue("Info", 0) == 1;
                onTop = (int)k.GetValue("OnTop", 1) == 1;
                themeMode = Math.Min(2, Math.Max(0, (int)k.GetValue("Theme", 0)));
            }
            catch { }
        }
    }

    void SaveSettings()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
            {
                k.SetValue("X", Location.X); k.SetValue("Y", Location.Y);
                k.SetValue("W", baseSize.Width); k.SetValue("H", baseSize.Height);
                k.SetValue("Opacity", (int)Math.Round(Opacity * 100));
                k.SetValue("Adapter", adapterId);
                k.SetValue("ClickThrough", clickThrough ? 1 : 0);
                k.SetValue("Bits", useBits ? 1 : 0);
                k.SetValue("Sys", showSys ? 1 : 0);
                k.SetValue("Info", showInfo ? 1 : 0);
                k.SetValue("OnTop", onTop ? 1 : 0);
                k.SetValue("Theme", themeMode);
            }
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnWinPrefChanged;
        NetworkChange.NetworkAddressChanged -= OnNetChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetAvailChanged;
        if (popup != null && !popup.IsDisposed) popup.Close();   // runs its final brightness write and releases the monitors
        SaveSettings(); tray.Visible = false; base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fHead.Dispose(); fName.Dispose(); fAxis.Dispose(); fSys.Dispose();
            var cc = cpuCounter;
            if (cc != null) cc.Dispose();
        }
        base.Dispose(disposing);
    }
}

class AboutForm : Form
{
    public AboutForm(Icon icon)
    {
        Text = "About " + AppInfo.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(340, 224);
        Font = new Font("Segoe UI", 9f);
        if (icon != null) Icon = icon;

        var pic = new PictureBox { Location = new Point(16, 16), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
        if (icon != null) pic.Image = new Icon(icon, 48, 48).ToBitmap();
        Controls.Add(pic);

        Controls.Add(new Label { Text = AppInfo.Name, Font = new Font("Segoe UI Semibold", 14f), Location = new Point(76, 14), AutoSize = true });
        Controls.Add(new Label { Text = "Version " + AppInfo.Version, Location = new Point(78, 44), AutoSize = true, ForeColor = Color.DimGray });
        Controls.Add(new Label
        {
            Text = "A tiny always-on-top desktop companion: live network speed, with optional CPU, RAM, disk and battery readouts, a monitor brightness slider and a Windows light/dark switch. Free and open source (MIT).",
            Location = new Point(16, 76), Size = new Size(308, 62)
        });

        int y = 146;
        AddLink("Made by Ahmed \u2013 mrobaidi.com", AppInfo.Website, ref y);
        if (AppInfo.Repo != "") AddLink("Source code on GitHub", AppInfo.Repo, ref y);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(250, 188), Size = new Size(75, 26) };
        Controls.Add(ok);
        AcceptButton = ok; CancelButton = ok;
    }

    void AddLink(string text, string url, ref int y)
    {
        var l = new LinkLabel { Text = text, Location = new Point(16, y), AutoSize = true };
        l.LinkClicked += delegate { try { System.Diagnostics.Process.Start(url); } catch { } };
        Controls.Add(l);
        y += 22;
    }
}
