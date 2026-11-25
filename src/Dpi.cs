// DPI helpers. The process is system-DPI-aware (declared in Program.Main), so the scale is fixed for the whole run.
// Every pixel size, position and padding goes through Dpi.S(); fonts are in points and scale by themselves.
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

static class Dpi
{
    public static readonly float Scale = Read();

    static float Read()
    {
        try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
        catch { return 1f; }
    }

    public static int S(int px) { return (int)Math.Round(px * Scale); }
    public static float S(float px) { return px * Scale; }
    public static Size S(Size s) { return new Size(S(s.Width), S(s.Height)); }
    public static Padding S(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }
    // Physical pixels back to 96-dpi units (for values saved to the registry).
    public static int Unscale(int px) { return (int)Math.Round(px / Scale); }

    // Call before any window is created. Windows 8.1+ first, Vista+ fallback.
    public static void Declare()
    {
        try { SetProcessDpiAwareness(1); return; }   // 1 = system aware
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch { return; }                            // already set (E_ACCESSDENIED): leave it
        try { SetProcessDPIAware(); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

// The UI font: Segoe UI Variable Text (Windows 11) if installed, else Segoe UI.
static class UiFont
{
    public static readonly string Family = Pick();

    // GDI+ silently substitutes a missing family, so the only proof is comparing the name it gives back.
    static bool Has(string family, float size)
    {
        try { using (var f = new Font(family, size)) return f.Name == family; }
        catch { return false; }
    }

    static string Pick() { return Has("Segoe UI Variable Text", 9f) ? "Segoe UI Variable Text" : "Segoe UI"; }

    public static Font Make(float size) { return new Font(Family, size); }

    // Semibold has its own family name in GDI+; fall back to the Segoe UI one, then to bold.
    public static Font Semibold(float size)
    {
        string v = Family + " Semibold";
        if (Has(v, size)) return new Font(v, size);
        if (Has("Segoe UI Semibold", size)) return new Font("Segoe UI Semibold", size);
        return new Font(Family, size, FontStyle.Bold);
    }
}
