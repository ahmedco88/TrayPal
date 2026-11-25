// The settings window: a sidebar of pages, each setting a row in a rounded card (Windows 11 style).
// Every control reads from the MeterForm's public settings API and writes straight back, so changes are live.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class SettingsForm : Form
{
    readonly MeterForm owner;
    readonly ListBox side = new ListBox();
    readonly Panel[] pages = new Panel[5];
    readonly string[] pageNames = { "General", "Meter", "Displays", "Focus timer", "About" };
    readonly Font fTitle = UiFont.Semibold(16f);
    readonly Font fSide = UiFont.Make(10f);
    static readonly Size[] sizes = { new Size(200, 90), new Size(240, 110), new Size(320, 150) };

    // Set while controls are being filled from the owner, so filling them never writes back
    // (a write could, for example, pop the click-through balloon).
    bool loading;
    bool isLight;
    Panel cur;           // page being built
    int rowY;
    Color bg, card, fg, dim, edge, accent;

    ComboBox adapterBox, themeBox, sizeBox;
    Button winThemeBtn;
    internal NumericUpDown focusMin, breakMin;   // built unwired (25 and 5)

    sealed class AdapterItem
    {
        public string Id, Text;
        public override string ToString() { return Text; }
    }

    // A rounded card the setting rows sit in.
    sealed class Card : Panel
    {
        public Color Fill, Edge;
        public Card() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent == null ? BackColor : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Dpi.S(8)))
            using (var b = new SolidBrush(Fill))
            using (var p = new Pen(Edge))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
        static GraphicsPath Round(RectangleF r, float d)
        {
            var p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    public SettingsForm(MeterForm owner)
    {
        this.owner = owner;
        Text = "TrayPal settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;   // sizes are scaled once, by Dpi.S, never by WinForms as well
        ClientSize = Dpi.S(new Size(640, 480));
        Font = UiFont.Make(9f);
        if (owner.Icon != null) Icon = owner.Icon;
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        side.Bounds = new Rectangle(0, 0, Dpi.S(160), Dpi.S(480));
        side.BorderStyle = BorderStyle.None;
        side.DrawMode = DrawMode.OwnerDrawFixed;
        side.ItemHeight = Dpi.S(36);
        side.IntegralHeight = false;
        side.DrawItem += DrawSide;
        side.AccessibleName = "Settings pages";
        foreach (var n in pageNames) side.Items.Add(n);
        Controls.Add(side);

        loading = true;
        BuildGeneral();
        BuildMeter();
        BuildDisplays();
        BuildFocus();
        BuildAbout();
        loading = false;

        side.SelectedIndexChanged += delegate { ShowPage(side.SelectedIndex); };
        ApplyTheme(owner.IsLight);
        side.SelectedIndex = 0;
    }

    // ---------- pages ----------
    Panel NewPage(int index, string title)
    {
        var p = new Panel { Bounds = new Rectangle(Dpi.S(160), 0, Dpi.S(480), Dpi.S(480)), Visible = false };
        p.Controls.Add(new Label { Text = title, Font = fTitle, AutoSize = true, Location = new Point(Dpi.S(22), Dpi.S(14)) });
        pages[index] = p;
        Controls.Add(p);
        cur = p;
        rowY = Dpi.S(64);
        return p;
    }

    // One setting: label left, control right. 'reserve' leaves room on the right (for a value label).
    Card Row(string text, Control c, int reserve)
    {
        var row = new Card { Bounds = new Rectangle(Dpi.S(24), rowY, Dpi.S(432), Dpi.S(42)) };
        var l = new Label { Text = text, AutoSize = true };
        l.Location = new Point(Dpi.S(14), Dpi.S(12));
        row.Controls.Add(l);
        if (string.IsNullOrEmpty(c.AccessibleName)) c.AccessibleName = text;
        c.Left = row.Width - Dpi.S(14) - Dpi.S(reserve) - c.Width;
        c.Top = (row.Height - c.Height) / 2;
        row.Controls.Add(c);
        cur.Controls.Add(row);
        rowY += Dpi.S(48);
        return row;
    }

    Card Row(string text, Control c) { return Row(text, c, 0); }

    Toggle AddToggle(string text, bool on, Action<bool> set)
    {
        var t = new Toggle { Checked = on, Size = new Size(Dpi.S(44), Dpi.S(24)) };
        t.CheckedChanged += delegate { if (!loading) set(t.Checked); };
        Row(text, t);
        return t;
    }

    ComboBox NewCombo(int width)
    {
        return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = Dpi.S(width) };
    }

    void BuildGeneral()
    {
        NewPage(0, "General");
        themeBox = NewCombo(170);
        themeBox.Items.AddRange(new object[] { "Dark", "Light", "Match Windows" });
        themeBox.SelectedIndex = owner.ThemeMode;
        themeBox.SelectedIndexChanged += delegate { if (!loading && themeBox.SelectedIndex >= 0) owner.ThemeMode = themeBox.SelectedIndex; };
        Row("Theme", themeBox);
        AddToggle("Start with Windows", owner.StartWithWindows, v => owner.StartWithWindows = v);
    }

    void BuildMeter()
    {
        NewPage(1, "Meter");
        AddToggle("Always on top", owner.AlwaysOnTop, v => owner.AlwaysOnTop = v);
        AddToggle("Click-through (ignore mouse)", owner.ClickThrough, v => owner.ClickThrough = v);
        AddToggle("Show in Mb/s (bits)", owner.UseBits, v => owner.UseBits = v);
        AddToggle("CPU + RAM graph", owner.ShowSys, v => owner.ShowSys = v);
        AddToggle("Disk + battery line", owner.ShowInfo, v => owner.ShowInfo = v);

        var value = new Label { AutoSize = false, Size = new Size(Dpi.S(34), Dpi.S(20)), TextAlign = ContentAlignment.MiddleRight, Text = owner.OpacityPercent + "%" };
        var op = new Slider(Dpi.S(8)) { Size = new Size(Dpi.S(150), Dpi.S(28)), Value = owner.OpacityPercent, AccessibleName = "Opacity" };
        op.ValueChanged += delegate
        {
            if (loading) return;
            if (op.Value < 40) { op.Value = 40; return; }   // a meter you cannot see cannot be found again
            owner.OpacityPercent = op.Value;
            value.Text = op.Value + "%";
        };
        var opRow = Row("Opacity", op, 38);
        value.Location = new Point(opRow.Width - Dpi.S(14) - value.Width, (opRow.Height - value.Height) / 2);
        opRow.Controls.Add(value);

        sizeBox = NewCombo(120);
        foreach (var s in sizes) sizeBox.Items.Add(s.Width + " x " + s.Height);
        sizeBox.SelectedIndex = Array.IndexOf(sizes, owner.MeterSize);
        sizeBox.SelectedIndexChanged += delegate { if (!loading && sizeBox.SelectedIndex >= 0) owner.MeterSize = sizes[sizeBox.SelectedIndex]; };
        Row("Size", sizeBox);

        adapterBox = NewCombo(240);
        adapterBox.DropDownWidth = Dpi.S(380);
        adapterBox.SelectedIndexChanged += delegate
        {
            var it = adapterBox.SelectedItem as AdapterItem;
            if (!loading && it != null) owner.AdapterId = it.Id;
        };
        Row("Adapter", adapterBox);
        FillAdapters();
    }

    void FillAdapters()
    {
        loading = true;
        adapterBox.Items.Clear();
        adapterBox.Items.Add(new AdapterItem { Id = "", Text = "Auto (main connection)" });
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
                 .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback && a.OperationalStatus == OperationalStatus.Up))
            adapterBox.Items.Add(new AdapterItem { Id = n.Id, Text = n.Name + " (" + n.Description + ")" });
        adapterBox.SelectedIndex = -1;
        for (int i = 0; i < adapterBox.Items.Count; i++)
            if (((AdapterItem)adapterBox.Items[i]).Id == owner.AdapterId) adapterBox.SelectedIndex = i;
        loading = false;
    }

    static string WinSwitchText() { return WinTheme.IsLight() ? "Switch Windows to dark mode" : "Switch Windows to light mode"; }

    void BuildDisplays()
    {
        var p = NewPage(2, "Displays");
        p.Controls.Add(new Label
        {
            Text = "Brightness, contrast and monitor volume are in the quick panel: left-click the tray icon. They need an external monitor with DDC/CI switched on.",
            Location = new Point(Dpi.S(24), Dpi.S(64)), Size = new Size(Dpi.S(432), Dpi.S(54)), Tag = "dim"
        });
        winThemeBtn = new Button { Text = WinSwitchText(), Size = new Size(Dpi.S(240), Dpi.S(32)), Location = new Point(Dpi.S(24), Dpi.S(128)), FlatStyle = FlatStyle.Flat };
        winThemeBtn.Click += delegate { WinTheme.Toggle(); winThemeBtn.Text = WinSwitchText(); };
        p.Controls.Add(winThemeBtn);
    }

    void BuildFocus()
    {
        NewPage(3, "Focus timer");
        focusMin = new NumericUpDown { Minimum = 1, Maximum = 180, Value = 25, Width = Dpi.S(70) };
        breakMin = new NumericUpDown { Minimum = 1, Maximum = 60, Value = 5, Width = Dpi.S(70) };
        focusMin.Value = Math.Max(1, Math.Min(180, owner.FocusMinutes));
        breakMin.Value = Math.Max(1, Math.Min(60, owner.BreakMinutes));
        focusMin.ValueChanged += delegate { owner.FocusMinutes = (int)focusMin.Value; };
        breakMin.ValueChanged += delegate { owner.BreakMinutes = (int)breakMin.Value; };
        Row("Focus length (minutes)", focusMin);
        Row("Break length (minutes)", breakMin);
    }

    void BuildAbout()
    {
        var p = NewPage(4, "About");
        var pic = new PictureBox { Location = new Point(Dpi.S(24), Dpi.S(64)), Size = new Size(Dpi.S(48), Dpi.S(48)), SizeMode = PictureBoxSizeMode.Zoom };
        if (owner.Icon != null) pic.Image = new Icon(owner.Icon, Dpi.S(48), Dpi.S(48)).ToBitmap();
        p.Controls.Add(pic);
        p.Controls.Add(new Label { Text = AppInfo.Name, Font = fTitle, AutoSize = true, Location = new Point(Dpi.S(84), Dpi.S(62)) });
        p.Controls.Add(new Label { Text = "Version " + AppInfo.Version, AutoSize = true, Location = new Point(Dpi.S(86), Dpi.S(96)), Tag = "dim" });
        p.Controls.Add(new Label
        {
            Text = "A tiny always-on-top desktop companion: live network speed, with optional CPU, RAM, disk and battery readouts, a monitor brightness slider and a Windows light/dark switch. Free and open source (MIT).",
            Location = new Point(Dpi.S(24), Dpi.S(128)), Size = new Size(Dpi.S(432), Dpi.S(62))
        });
        int y = Dpi.S(200);
        AddLink(p, "Made by Ahmed – mrobaidi.com", AppInfo.Website, ref y);
        if (AppInfo.Repo != "") AddLink(p, "Source code on GitHub", AppInfo.Repo, ref y);
    }

    static void AddLink(Panel p, string text, string url, ref int y)
    {
        var l = new LinkLabel { Text = text, Location = new Point(Dpi.S(24), y), AutoSize = true };
        l.LinkClicked += delegate { try { System.Diagnostics.Process.Start(url); } catch { } };
        p.Controls.Add(l);
        y += Dpi.S(24);
    }

    void ShowPage(int i)
    {
        if (i < 0) return;
        if (i == 1) FillAdapters();
        if (i == 2) winThemeBtn.Text = WinSwitchText();
        for (int k = 0; k < pages.Length; k++) pages[k].Visible = k == i;
    }

    // ---------- sidebar ----------
    void DrawSide(object s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
        var r = new Rectangle(e.Bounds.X + Dpi.S(8), e.Bounds.Y + Dpi.S(2), e.Bounds.Width - Dpi.S(16), e.Bounds.Height - Dpi.S(4));
        if (sel)
        {
            using (var b = new SolidBrush(card)) e.Graphics.FillRectangle(b, r);
            using (var b = new SolidBrush(accent)) e.Graphics.FillRectangle(b, r.X, r.Y + Dpi.S(8), Dpi.S(3), r.Height - Dpi.S(16));
        }
        TextRenderer.DrawText(e.Graphics, pageNames[e.Index], fSide, new Rectangle(r.X + Dpi.S(14), r.Y, r.Width - Dpi.S(14), r.Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    // ---------- theme ----------
    public void ApplyTheme(bool light)
    {
        isLight = light;
        bg = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
        card = light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(43, 43, 43);
        fg = light ? Color.FromArgb(27, 27, 27) : Color.FromArgb(240, 240, 240);
        dim = light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(184, 184, 184);
        edge = light ? Color.FromArgb(208, 208, 208) : Color.FromArgb(58, 58, 58);
        accent = light ? Color.FromArgb(176, 118, 0) : Color.FromArgb(245, 179, 1);
        BackColor = bg;
        ForeColor = fg;
        side.BackColor = bg;
        foreach (Control c in Controls) Walk(c);
        side.Invalidate();
        SetTitleBar();
        Invalidate(true);
    }

    void Walk(Control c)
    {
        bool inCard = c.Parent is Card;
        Color back = inCard ? card : bg;
        if (c is Card) { var k = (Card)c; k.Fill = card; k.Edge = edge; k.BackColor = bg; }
        else if (c is Slider)
        {
            var s = (Slider)c;
            s.BackColor = back;
            s.Accent = accent;
            s.Rail = isLight ? Color.FromArgb(138, 138, 138) : Color.FromArgb(158, 158, 158);
            s.ThumbBack = isLight ? Color.White : Color.FromArgb(69, 69, 69);
            s.ThumbRing = isLight ? Color.FromArgb(160, 160, 160) : Color.FromArgb(80, 80, 80);
        }
        else if (c is Toggle)
        {
            var t = (Toggle)c;
            t.BackColor = back; t.Accent = accent; t.Fg = dim;
            t.Invalidate();
        }
        else if (c is LinkLabel)
        {
            var l = (LinkLabel)c;
            l.BackColor = back; l.LinkColor = accent; l.ActiveLinkColor = accent; l.VisitedLinkColor = accent;
        }
        else if (c is Label)
        {
            c.BackColor = back;
            c.ForeColor = "dim".Equals(c.Tag) ? dim : fg;
        }
        else if (c is Button)
        {
            var b = (Button)c;
            b.BackColor = card; b.ForeColor = fg;
            b.FlatAppearance.BorderColor = edge;
            b.FlatAppearance.MouseOverBackColor = isLight ? Color.FromArgb(240, 240, 240) : Color.FromArgb(55, 55, 55);
        }
        else if (c is ComboBox || c is NumericUpDown)
        {
            c.BackColor = isLight ? Color.White : Color.FromArgb(55, 55, 55);
            c.ForeColor = fg;
        }
        else if (c is ListBox) { }
        else if (c is Panel) c.BackColor = bg;
        foreach (Control child in c.Controls) Walk(child);
    }

    void SetTitleBar()
    {
        if (!IsHandleCreated) return;
        int dark = isLight ? 0 : 1;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }   // ignored on older Windows
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetTitleBar();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fTitle.Dispose(); fSide.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

// Windows 11 style on/off switch: a pill with a sliding dot, accent when on. Click or Space toggles it.
sealed class Toggle : Control
{
    bool on, hot;

    public Color Accent { get; set; }
    public Color Fg { get; set; }
    public event EventHandler CheckedChanged;

    public Toggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        Accent = Color.FromArgb(245, 179, 1);
        Fg = Color.Gray;
    }

    public bool Checked
    {
        get { return on; }
        set
        {
            if (on == value) return;
            on = value;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Dpi.S(40), h = Dpi.S(20), x = (Width - w) / 2f, y = (Height - h) / 2f;
        using (var path = new GraphicsPath())
        {
            path.AddArc(x, y, h, h, 90, 180);
            path.AddArc(x + w - h, y, h, h, 270, 180);
            path.CloseFigure();
            if (on) using (var b = new SolidBrush(Accent)) g.FillPath(b, path);
            else using (var p = new Pen(Fg, Dpi.S(1.5f))) g.DrawPath(p, path);
        }
        float d = Dpi.S(hot ? 14 : 12);
        float cx = on ? x + w - h / 2f : x + h / 2f;
        using (var b = new SolidBrush(on ? Color.FromArgb(27, 27, 27) : Fg))
            g.FillEllipse(b, cx - d / 2f, y + h / 2f - d / 2f, d, d);
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Fg) { DashStyle = DashStyle.Dot })
                g.DrawRectangle(p, x - Dpi.S(3), y - Dpi.S(3), w + Dpi.S(5), h + Dpi.S(5));
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !on;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !on; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
}
