// Windows light/dark mode: the same two values the Settings app writes.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class WinTheme
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Apps mode: what most apps follow.
    public static bool IsLight() { return Read("AppsUseLightTheme"); }

    static bool Read(string name)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v != 0 : true;
            }
        }
        catch { return true; }
    }

    public static void Set(bool light)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            k.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        }
        // Tell running apps (Explorer, the taskbar) to repaint now.
        IntPtr result;
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out result);
    }

    public static void Toggle() { Set(!IsLight()); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeoutMs, out IntPtr result);
}
