// External monitor brightness over DDC/CI: the same channel the monitor's own buttons use.
// Laptop panels (WMI) are not handled.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class DisplayMonitor
{
    public IntPtr Handle;
    public string Name;
    public bool Supported;
    public uint Min, Max;
    public int Percent;

    public DisplayMonitor(IntPtr handle, string name) { Handle = handle; Name = name; }
}

static class Brightness
{
    // Every screen's physical monitors, with brightness read fresh. Caller must Close() the list.
    public static List<DisplayMonitor> Open()
    {
        var list = new List<DisplayMonitor>();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            var pt = new POINT();
            pt.X = b.X + b.Width / 2; pt.Y = b.Y + b.Height / 2;
            IntPtr hMon = MonitorFromPoint(pt, 2);
            uint count;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) continue;
            var arr = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, arr)) continue;
            foreach (var p in arr)
            {
                var m = new DisplayMonitor(p.Handle, "Display " + (i + 1) + (screens[i].Primary ? " (main)" : ""));
                uint min, cur, max;
                Pace();
                bool ok = GetMonitorBrightness(p.Handle, out min, out cur, out max);
                Stamp();
                if (ok && max > min)
                {
                    m.Supported = true;
                    m.Min = min;
                    m.Max = max;
                    m.Percent = (int)Math.Round((cur - min) * 100.0 / (max - min));
                }
                list.Add(m);
            }
        }
        return list;
    }

    public static bool Set(DisplayMonitor m, int percent)
    {
        Pace();
        bool ok = SetMonitorBrightness(m.Handle, m.Min + (uint)Math.Round(percent * (m.Max - m.Min) / 100.0));
        Stamp();
        return ok;
    }

    public static void Close(List<DisplayMonitor> list)
    {
        foreach (var m in list) DestroyPhysicalMonitor(m.Handle);
        list.Clear();
    }

    // DDC/CI monitors silently drop a command (read or write) sent within ~50 ms of the previous one,
    // with no error, so every command waits until 60 ms have passed since the last one.
    // Environment.TickCount64 does not exist on .NET Framework, hence the Stopwatch.
    const int MinGapMs = 60;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static long lastCommand = -MinGapMs;

    static void Pace()
    {
        int wait = MinGapMs - (int)(clock.ElapsedMilliseconds - lastCommand);
        if (wait > 0) System.Threading.Thread.Sleep(wait);
    }

    static void Stamp() { lastCommand = clock.ElapsedMilliseconds; }

    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitor(IntPtr h);
}
