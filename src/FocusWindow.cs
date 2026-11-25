// Small always-on-top control window for the focus timer: state, countdown, +5 min, pause/resume, sound, stop.
// Not owned by the meter, so it stays visible when the meter is hidden. MeterForm's 1 s tick calls UpdateView().
using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class FocusWindow : Form
{
    const string Add = "", Pause = "", Play = "", Stop = "";

    readonly MeterForm owner;
    readonly FocusTimer timer;
    readonly Font stateFont = UiFont.Make(9.5f);
    readonly Font timeFont = UiFont.Semibold(26f);
    readonly Font iconFont = Icons.Create(12f);
    readonly ToolTip tips = new ToolTip();
    readonly IconButton addBtn = new IconButton(), pauseBtn = new IconButton(), soundBtn = new IconButton(), stopBtn = new IconButton();
    Color fg, dim, accent, border;
    bool lastPaused, lastSound;

    // Raised after the user drags the window; the new top-left in screen pixels.
    public event Action<Point> Moved;

    public FocusWindow(MeterForm owner, FocusTimer timer, bool light, Rectangle meterBounds, Point? saved)
    {
        this.owner = owner;
        this.timer = timer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Text = AppInfo.Name + " focus";
        Size = new Size(Dpi.S(236), Dpi.S(126));

        int btn = Dpi.S(32), gap = Dpi.S(10);
        int x = (Width - (4 * btn + 3 * gap)) / 2, y = Dpi.S(82);
        var all = new[] { addBtn, pauseBtn, soundBtn, stopBtn };
        for (int i = 0; i < all.Length; i++)
        {
            var b = all[i];
            b.Font = iconFont;
            b.Size = new Size(btn, btn);
            b.Location = new Point(x + i * (btn + gap), y);
            Controls.Add(b);
        }
        addBtn.Glyph = Add;
        stopBtn.Glyph = Stop;
        addBtn.Click += delegate { owner.FocusAddFive(); UpdateView(); };
        pauseBtn.Click += delegate { owner.FocusTogglePause(); UpdateView(); };
        soundBtn.Click += delegate { owner.FocusSound = !owner.FocusSound; UpdateView(); };
        stopBtn.Click += delegate { owner.ToggleFocus(); };   // same as stopping from the menu
        tips.SetToolTip(addBtn, "Add 5 minutes");
        tips.SetToolTip(stopBtn, "Stop focus timer");
        addBtn.AccessibleName = "Add 5 minutes";
        stopBtn.AccessibleName = "Stop focus timer";

        Restyle(light);
        lastPaused = !timer.Paused; lastSound = !owner.FocusSound;   // force the first UpdateView to set glyphs
        UpdateView();
        Location = Place(meterBounds, saved);
    }

    // Remembered spot if it is on a screen, else just below the meter (right-aligned), else above it; always inside the work area.
    Point Place(Rectangle meter, Point? saved)
    {
        if (saved.HasValue && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(saved.Value)))
            return Clamp(saved.Value, Screen.FromPoint(saved.Value).WorkingArea);
        var wa = Screen.FromRectangle(meter).WorkingArea;
        int gap = Dpi.S(8);
        int x = meter.Right - Width, y = meter.Bottom + gap;
        if (y + Height > wa.Bottom) y = meter.Top - Height - gap;
        return Clamp(new Point(x, y), wa);
    }

    Point Clamp(Point p, Rectangle wa)
    {
        return new Point(Math.Max(wa.Left, Math.Min(p.X, wa.Right - Width)), Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - Height)));
    }

    public void Restyle(bool light)
    {
        fg = light ? Color.FromArgb(27, 27, 27) : Color.FromArgb(240, 240, 240);
        dim = light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(184, 184, 184);
        border = light ? Color.FromArgb(208, 208, 208) : Color.FromArgb(58, 58, 58);
        accent = light ? Color.FromArgb(176, 118, 0) : Color.FromArgb(245, 179, 1);
        BackColor = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
        var hover = light ? Color.FromArgb(15, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255);
        foreach (var b in new[] { addBtn, pauseBtn, soundBtn, stopBtn })
        {
            b.Fg = fg; b.Accent = accent; b.Hover = hover; b.BackColor = BackColor; b.Invalidate();
        }
        if (IsHandleCreated) SetBorder();
        Invalidate();
    }

    // Called once a second by MeterForm (and after a button click).
    public void UpdateView()
    {
        if (timer.Paused != lastPaused)
        {
            lastPaused = timer.Paused;
            pauseBtn.Glyph = lastPaused ? Play : Pause;
            string tip = lastPaused ? "Resume" : "Pause";
            tips.SetToolTip(pauseBtn, tip);
            pauseBtn.AccessibleName = tip;
        }
        if (owner.FocusSound != lastSound)
        {
            lastSound = owner.FocusSound;
            soundBtn.Glyph = lastSound ? Icons.Volume : Icons.Muted;
            string tip = lastSound ? "Sound on (click to mute)" : "Sound off (click to unmute)";
            tips.SetToolTip(soundBtn, tip);
            soundBtn.AccessibleName = tip;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        string state = timer.Paused ? "Paused" : timer.OnBreak ? "Break" : "Focus";
        const TextFormatFlags f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, state, stateFont, new Rectangle(0, Dpi.S(10), Width, Dpi.S(22)), dim, f);
        TextRenderer.DrawText(e.Graphics, timer.Countdown ?? "", timeFont, new Rectangle(0, Dpi.S(30), Width, Dpi.S(48)), timer.Paused ? dim : accent, f);
    }

    // Drag from anywhere that is not a button (the buttons are child controls and take their own clicks).
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 2, 0);   // WM_NCLBUTTONDOWN, HTCAPTION: runs until the mouse is released
        if (Moved != null) Moved(Location);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }   // WS_EX_TOOLWINDOW: no Alt-Tab entry
    }

    // Windows 11 rounded corners and a hairline border, like the quick panel (ignored on Windows 10).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        SetBorder();
    }

    void SetBorder()
    {
        int colour = ColorTranslator.ToWin32(border);
        DwmSetWindowAttribute(Handle, 34, ref colour, sizeof(int));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tips.Dispose();
        stateFont.Dispose();
        timeFont.Dispose();
        iconFont.Dispose();
        base.OnFormClosed(e);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
}
