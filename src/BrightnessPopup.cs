// The quick panel: per screen brightness, contrast and monitor volume, PC sound, dark mode, focus and settings
// buttons, near where you clicked. Closes on click-away or Esc.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// One slider's target on one monitor: a VCP code (0x10 brightness, 0x12 contrast, 0x62 volume).
// Used as a dictionary key by reference, so each slider makes exactly one.
sealed class VcpTarget
{
    public DisplayMonitor M;
    public byte Code;
}

sealed class BrightnessPopup : Form
{
    const byte VcpBrightness = 0x10;
    // ponytail: slider size is fixed; the PC sound device name ellipsizes instead of widening the panel.
    static int SliderW { get { return Dpi.S(220); } }
    static int ValueW { get { return Dpi.S(34); } }
    static int IconW { get { return Dpi.S(28); } }

    readonly List<DisplayMonitor> monitors = Brightness.Open();
    readonly Dictionary<VcpTarget, int> pending = new Dictionary<VcpTarget, int>();
    // DDC/CI is slow and some monitors choke on a flood, so a drag sends at most 10 writes a second.
    // One flush covers brightness, contrast and monitor volume. Nothing writes to a monitor outside Flush().
    // ponytail: writes run on the UI thread; move to a background task if a monitor makes dragging sticky.
    readonly Timer flush = new Timer();
    readonly Timer offTimer = new Timer();
    readonly Point anchor;
    readonly Color border;
    readonly Font valueFont = UiFont.Make(10.5f);
    readonly Font iconFont = Icons.Create(12f);
    readonly ToolTip tips = new ToolTip();
    readonly bool light;
    readonly Color fg, dim, accent, hover, rail, thumbBack, thumbRing;
    bool screensOff;

    public BrightnessPopup(Point anchor, bool light) : this(anchor, light, null, null, null) { }

    // null openSettings hides the gear; null toggleFocus hides the focus button; focusRunning may be null (false).
    public BrightnessPopup(Point anchor, bool light, Action openSettings, Action toggleFocus, Func<bool> focusRunning)
    {
        this.anchor = anchor;
        this.light = light;
        flush.Interval = 100;
        fg = light ? Color.FromArgb(27, 27, 27) : Color.FromArgb(240, 240, 240);
        dim = light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(184, 184, 184);
        border = light ? Color.FromArgb(208, 208, 208) : Color.FromArgb(58, 58, 58);
        accent = light ? Color.FromArgb(176, 118, 0) : Color.FromArgb(245, 179, 1);
        hover = light ? Color.FromArgb(15, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255);
        rail = light ? Color.FromArgb(138, 138, 138) : Color.FromArgb(158, 158, 158);
        thumbBack = light ? Color.White : Color.FromArgb(69, 69, 69);
        thumbRing = light ? Color.FromArgb(160, 160, 160) : Color.FromArgb(80, 80, 80);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
        Font = UiFont.Make(9f);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = Dpi.S(16, 12, 16, 10) };
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            table.Controls.Add(IconLabel(Icons.Monitor));
            var title = new Label { Text = m.Name, ForeColor = dim, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Dpi.S(3, 2, 0, 2) };
            table.Controls.Add(title);
            if (i == 0)
            {
                // One button only: the Windows command turns every screen off, so one per monitor would mislead.
                var off = MakeButton(Icons.Power, "Turn screens off", false);
                off.Anchor = AnchorStyles.Right;
                off.Click += delegate { StartScreensOff(); };
                table.Controls.Add(off);
            }
            else table.Controls.Add(new Label { Size = new Size(1, 1), Margin = Padding.Empty });

            if (!m.Supported)
            {
                var na = new Label { Text = "Brightness not available", ForeColor = fg, AutoSize = true, Anchor = AnchorStyles.Left };
                table.Controls.Add(na);
                table.SetColumnSpan(na, 3);
                continue;
            }
            AddVcpRow(table, m, VcpBrightness, Icons.Brightness, " brightness", m.Percent);
            if (m.HasContrast) AddVcpRow(table, m, Brightness.VcpContrast, Icons.Contrast, " contrast", m.Contrast);
            if (m.HasVolume) AddVcpRow(table, m, Brightness.VcpVolume, Icons.Speaker, " speaker volume", m.Volume);
        }
        if (monitors.Count == 0)
        {
            var none = new Label { Text = "No screens found", ForeColor = fg, AutoSize = true };
            table.Controls.Add(none);
            table.SetColumnSpan(none, 3);
        }

        int vol; bool muted;
        if (Audio.TryGet(out vol, out muted))
        {
            AddDivider(table);
            string name = Audio.DeviceName();
            var head = new Label
            {
                Text = name == null ? "PC sound" : "PC sound · " + name, ForeColor = dim, AutoSize = false, AutoEllipsis = true,
                Size = new Size(1, Dpi.S(20)), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Dpi.S(3, 2, 3, 2),
            };
            table.Controls.Add(head);
            table.SetColumnSpan(head, 3);

            var mute = MakeButton(muted ? Icons.Muted : Icons.Volume, "Mute PC sound", muted);
            var value = ValueLabel(vol);
            var slider = MakeSlider("PC sound volume", vol);
            slider.ValueChanged += delegate { value.Text = slider.Value.ToString(); Audio.SetVolume(slider.Value); };
            slider.Committed += delegate { if (!mute.On) Ding(); };   // the level is already applied; silent while muted
            mute.Click += delegate
            {
                mute.On = !mute.On;
                mute.Glyph = mute.On ? Icons.Muted : Icons.Volume;
                Audio.SetMute(mute.On);
            };
            table.Controls.Add(mute);
            table.Controls.Add(slider);
            table.Controls.Add(value);
        }

        AddDivider(table);
        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        var dark = MakeButton(WinTheme.IsLight() ? Icons.Moon : Icons.Brightness, WinTheme.IsLight() ? "Switch Windows to dark mode" : "Switch Windows to light mode", false);
        dark.Click += delegate { WinTheme.Toggle(); Close(); };
        left.Controls.Add(dark);
        if (toggleFocus != null)
        {
            bool running = focusRunning != null && focusRunning();
            var focus = MakeButton(Icons.Focus, running ? "Stop focus timer" : "Start focus timer", running);
            focus.Click += delegate { toggleFocus(); Close(); };
            left.Controls.Add(focus);
        }
        table.Controls.Add(left);
        table.SetColumnSpan(left, 2);
        if (openSettings != null)
        {
            var gear = MakeButton(Icons.Gear, "Settings", false);
            gear.Anchor = AnchorStyles.Right;
            gear.Click += delegate { Close(); openSettings(); };
            table.Controls.Add(gear);
        }

        Controls.Add(table);
        ActiveControl = table.Controls.OfType<Slider>().FirstOrDefault(); // keys and wheel work straight away

        flush.Tick += delegate { Flush(); };
        flush.Start();
        offTimer.Interval = 500;
        offTimer.Tick += delegate { offTimer.Stop(); Brightness.ScreensOff(Handle); Close(); };
        Deactivate += delegate { if (!screensOff) Close(); };
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    // Hide, then wait half a second before switching off: the mouse-up of this click would wake the screens at once.
    void StartScreensOff()
    {
        screensOff = true;
        Hide();
        offTimer.Start();
    }

    Label IconLabel(string glyph)
    {
        return new Label
        {
            Text = glyph, Font = iconFont, ForeColor = dim, AutoSize = false, Size = new Size(IconW, Dpi.S(32)),
            TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.Left, Margin = Dpi.S(5, 3, 5, 3),
        };
    }

    Label ValueLabel(int pct)
    {
        return new Label
        {
            Text = pct.ToString(), ForeColor = fg, Font = valueFont, AutoSize = false,
            Size = new Size(ValueW, Dpi.S(24)), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right,
        };
    }

    Slider MakeSlider(string accessibleName, int pct)
    {
        return new Slider(Dpi.S(10))
        {
            Value = pct, AccessibleName = accessibleName, Size = new Size(SliderW, Dpi.S(32)), Anchor = AnchorStyles.Left, BackColor = BackColor,
            Accent = accent, Rail = rail, ThumbBack = thumbBack, ThumbRing = thumbRing,
        };
    }

    IconButton MakeButton(string glyph, string tip, bool on)
    {
        var b = new IconButton
        {
            Glyph = glyph, On = on, Font = iconFont, Fg = fg, Accent = accent, Hover = hover, BackColor = BackColor,
            AccessibleName = tip, Size = new Size(Dpi.S(32), Dpi.S(32)),
        };
        tips.SetToolTip(b, tip);
        return b;
    }

    void AddVcpRow(TableLayoutPanel table, DisplayMonitor m, byte code, string glyph, string what, int pct)
    {
        var target = new VcpTarget { M = m, Code = code };
        var value = ValueLabel(pct);
        var slider = MakeSlider(m.Name + what, pct);
        slider.ValueChanged += delegate { value.Text = slider.Value.ToString(); pending[target] = slider.Value; };
        if (code == Brightness.VcpVolume) slider.Committed += delegate { Flush(); Ding(); };   // write the new level first, then play
        table.Controls.Add(IconLabel(glyph));
        table.Controls.Add(slider);
        table.Controls.Add(value);
    }

    void AddDivider(TableLayoutPanel table)
    {
        var d = new Panel { BackColor = border, Size = new Size(1, 1), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Dpi.S(0, 6, 0, 6) };
        table.Controls.Add(d);
        table.SetColumnSpan(d, 3);
    }

    void Flush()
    {
        foreach (var kv in pending)
        {
            if (kv.Key.Code == VcpBrightness) Brightness.Set(kv.Key.M, kv.Value);
            else Brightness.SetVcp(kv.Key.M, kv.Key.Code, kv.Value);
        }
        pending.Clear();
    }

    // Windows' default beep, so the level can be heard after a volume slider is let go.
    static void Ding() { System.Media.SystemSounds.Beep.Play(); }

    static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

    // Near the click, kept inside the screen's usable area (whichever edge the taskbar is on).
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int gap = Dpi.S(12);
        int x = Clamp(anchor.X - Width / 2, wa.Left + gap, wa.Right - Width - gap);
        int y = Clamp(anchor.Y - Height - gap, wa.Top + gap, wa.Bottom - Height - gap);
        Location = new Point(x, y);
        SetForegroundWindow(Handle);
    }

    // Windows 11 rounded corners and a hairline border, like the system flyouts (ignored on Windows 10).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, colour = ColorTranslator.ToWin32(border);
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(Handle, 34, ref colour, sizeof(int));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        flush.Stop();
        offTimer.Stop();
        Flush(); // the last value of a drag always lands
        flush.Dispose();
        offTimer.Dispose();
        Brightness.Close(monitors);
        tips.Dispose();
        valueFont.Dispose();
        iconFont.Dispose();
        base.OnFormClosed(e);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
}

// Icon glyphs from the font Windows ships: Segoe Fluent Icons (Windows 11), else Segoe MDL2 Assets (Windows 10).
static class Icons
{
    public const string Brightness = "", Contrast = "", Speaker = "", Volume = "", Muted = "";
    public const string Power = "", Moon = "", Focus = "", Gear = "", Monitor = "";

    public static Font Create(float size)
    {
        var f = new Font("Segoe Fluent Icons", size);
        if (f.Name == "Segoe Fluent Icons") return f;
        f.Dispose(); // GDI+ silently substitutes a missing font, so compare the name
        return new Font("Segoe MDL2 Assets", size);
    }
}

// Flat glyph button in the Slider pattern: rounded hover fill, accent colour while On.
// Control raises Click itself on mouse-up, so only Space/Enter call OnClick here.
sealed class IconButton : Control
{
    bool hot;
    string glyph;
    bool on;

    public Color Fg { get; set; }
    public Color Accent { get; set; }
    public Color Hover { get; set; }

    public IconButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Size = new Size(Dpi.S(32), Dpi.S(32));
    }

    public string Glyph { get { return glyph; } set { glyph = value; Invalidate(); } }
    public bool On { get { return on; } set { on = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (hot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            using (var fill = new SolidBrush(Hover))
            {
                int r = Dpi.S(8), w = Width - 1, h = Height - 1;
                path.AddArc(0, 0, r, r, 180, 90);
                path.AddArc(w - r, 0, r, r, 270, 90);
                path.AddArc(w - r, h - r, r, r, 0, 90);
                path.AddArc(0, h - r, r, r, 90, 90);
                path.CloseFigure();
                g.FillPath(fill, path);
            }
        }
        TextRenderer.DrawText(g, glyph ?? "", Font, ClientRectangle, on ? Accent : Fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, new Rectangle(Dpi.S(2), Dpi.S(2), Width - Dpi.S(4), Height - Dpi.S(4)));
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys k) { return k == Keys.Space || k == Keys.Enter || base.IsInputKey(k); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        else base.OnKeyDown(e);
    }
}

// Windows 11 style 0-100 slider: thin rail, accent fill, round thumb whose dot grows on hover.
// Drag or click, mouse wheel (2 per notch, like the volume flyout), arrows, PgUp/PgDn, Home/End.
sealed class Slider : Control
{
    readonly int radius;
    int value;
    bool hot;

    public Color Accent { get; set; }
    public Color Rail { get; set; }
    public Color ThumbBack { get; set; }
    public Color ThumbRing { get; set; }
    public event EventHandler ValueChanged;
    // Raised once the user has finished: mouse released, or 300 ms after the last key or wheel input.
    public event EventHandler Committed;
    readonly Timer settle = new Timer();
    bool dragging;

    public Slider(int thumbRadius)
    {
        radius = thumbRadius;
        settle.Interval = 300;
        settle.Tick += delegate { settle.Stop(); RaiseCommitted(); };
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
    }

    public int Value
    {
        get { return value; }
        set
        {
            int v = Math.Max(0, Math.Min(100, value));
            if (v == this.value) return;
            this.value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    void RaiseCommitted() { if (Committed != null) Committed(this, EventArgs.Empty); }

    void Settle() { settle.Stop(); settle.Start(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) settle.Dispose();
        base.Dispose(disposing);
    }

    float Left0 { get { return radius + 1; } }
    float Width0 { get { return Width - 2 * (radius + 1); } }
    float ThumbX { get { return Left0 + Width0 * value / 100f; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float y = Height / 2f, t = Math.Max(Dpi.S(2f), radius * 0.4f);
        using (var rail = new Pen(Rail, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(rail, Left0, y, Left0 + Width0, y);
        using (var fill = new Pen(Accent, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fill, Left0, y, ThumbX, y);

        float x = ThumbX, r = radius;
        using (var ring = new SolidBrush(ThumbRing)) g.FillEllipse(ring, x - r, y - r, 2 * r, 2 * r);
        using (var back = new SolidBrush(ThumbBack)) g.FillEllipse(back, x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2);
        float d = r * (hot || Capture ? 0.62f : 0.48f);
        using (var dot = new SolidBrush(Accent)) g.FillEllipse(dot, x - d, y - d, 2 * d, 2 * d);
    }

    void SetFromX(int x) { Value = (int)Math.Round((x - Left0) * 100 / Width0); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Capture = true;
        dragging = true;
        SetFromX(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Capture) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        Capture = false;
        Invalidate();
        if (dragging) { dragging = false; settle.Stop(); RaiseCommitted(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta / 120 * 2; Settle(); }

    protected override bool IsInputKey(Keys k)
    {
        return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Right: case Keys.Up: Value += 1; break;
            case Keys.Left: case Keys.Down: Value -= 1; break;
            case Keys.PageUp: Value += 10; break;
            case Keys.PageDown: Value -= 10; break;
            case Keys.Home: Value = 0; break;
            case Keys.End: Value = 100; break;
            default: return;
        }
        Settle();
    }
}
