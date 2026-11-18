// The brightness card: one slider per screen, near where you clicked. Closes on click-away or Esc.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class BrightnessPopup : Form
{
    readonly List<DisplayMonitor> monitors = Brightness.Open();
    readonly Dictionary<DisplayMonitor, int> pending = new Dictionary<DisplayMonitor, int>();
    // DDC/CI is slow and some monitors choke on a flood, so a drag sends at most 10 writes a second.
    // ponytail: writes run on the UI thread; move to a background task if a monitor makes dragging sticky.
    readonly Timer flush = new Timer();
    readonly Point anchor;
    readonly Color border;
    readonly Font valueFont = new Font("Segoe UI", 10.5f);

    public BrightnessPopup(Point anchor, bool light)
    {
        this.anchor = anchor;
        flush.Interval = 100;
        var fg = light ? Color.FromArgb(27, 27, 27) : Color.FromArgb(240, 240, 240);
        var dim = light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(184, 184, 184);
        border = light ? Color.FromArgb(208, 208, 208) : Color.FromArgb(58, 58, 58);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
        Font = new Font("Segoe UI", 9f);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 12, 16, 10) };
        foreach (var mon in monitors)
        {
            var m = mon;
            var title = new Label { Text = m.Name, ForeColor = dim, AutoSize = true, Margin = new Padding(2, 2, 0, 2) };
            table.Controls.Add(title);
            table.SetColumnSpan(title, 2);
            if (!m.Supported)
            {
                var na = new Label { Text = "Brightness not available", ForeColor = fg, AutoSize = true, Anchor = AnchorStyles.Left };
                table.Controls.Add(na);
                table.SetColumnSpan(na, 2);
                continue;
            }
            var value = new Label
            {
                Text = m.Percent.ToString(), ForeColor = fg, Font = valueFont, AutoSize = false,
                Size = new Size(34, 24), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right,
            };
            var slider = new Slider(10)
            {
                Value = m.Percent, AccessibleName = m.Name + " brightness", Size = new Size(220, 32), Anchor = AnchorStyles.Left, BackColor = BackColor,
                Accent = light ? Color.FromArgb(176, 118, 0) : Color.FromArgb(245, 179, 1),
                Rail = light ? Color.FromArgb(138, 138, 138) : Color.FromArgb(158, 158, 158),
                ThumbBack = light ? Color.White : Color.FromArgb(69, 69, 69),
                ThumbRing = light ? Color.FromArgb(160, 160, 160) : Color.FromArgb(80, 80, 80),
            };
            slider.ValueChanged += delegate { value.Text = slider.Value.ToString(); pending[m] = slider.Value; };
            table.Controls.Add(slider);
            table.Controls.Add(value);
        }
        if (monitors.Count == 0)
        {
            var none = new Label { Text = "No screens found", ForeColor = fg, AutoSize = true };
            table.Controls.Add(none);
            table.SetColumnSpan(none, 2);
        }
        Controls.Add(table);
        ActiveControl = table.Controls.OfType<Slider>().FirstOrDefault(); // keys and wheel work straight away

        flush.Tick += delegate { Flush(); };
        flush.Start();
        Deactivate += delegate { Close(); };
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    void Flush()
    {
        foreach (var kv in pending) Brightness.Set(kv.Key, kv.Value);
        pending.Clear();
    }

    static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

    // Near the click, kept inside the screen's usable area (whichever edge the taskbar is on).
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromPoint(anchor).WorkingArea;
        const int gap = 12;
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
        Flush(); // the last value of a drag always lands
        flush.Dispose();
        Brightness.Close(monitors);
        valueFont.Dispose();
        base.OnFormClosed(e);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
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

    public Slider(int thumbRadius)
    {
        radius = thumbRadius;
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

    float Left0 { get { return radius + 1; } }
    float Width0 { get { return Width - 2 * (radius + 1); } }
    float ThumbX { get { return Left0 + Width0 * value / 100f; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float y = Height / 2f, t = Math.Max(2f, radius * 0.4f);
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
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta / 120 * 2; }

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
        }
    }
}
