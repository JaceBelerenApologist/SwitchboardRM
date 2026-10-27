using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>Status colours for text such as the sign-in status.</summary>
internal enum Tone { Good, Warn, Bad }

/// <summary>The colours for one look. Both looks share the purple accent.</summary>
internal sealed class Palette
{
    public bool IsDark { get; init; }
    public Color Back { get; init; }        // window and panel background
    public Color Surface { get; init; }     // list, text boxes, menus
    public Color Raised { get; init; }      // toolbar, status bar, column headers, tab strip
    public Color Border { get; init; }
    public Color Text { get; init; }
    public Color Muted { get; init; }       // offline servers, hints, disabled text
    public Color Hover { get; init; }       // hovered buttons and menu items (purple tint)
    public Color Accent { get; init; }      // selection, checked buttons, active tab bar
    public Color AccentFore { get; init; }  // purple used as a text colour (readable on Raised)
    public Color AccentHover { get; init; } // primary button under the mouse
    public Color AccentText { get; init; }  // text drawn on top of Accent
    public Color Good { get; init; }
    public Color Warn { get; init; }
    public Color Bad { get; init; }

    private static Color Hex(string s) => ColorTranslator.FromHtml(s);

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Back = Hex("#F0F0F2"),
        Surface = Hex("#FFFFFF"),
        Raised = Hex("#F7F7F9"),
        Border = Hex("#D6D6DD"),
        Text = Hex("#1B1B1F"),
        Muted = Hex("#7A7A86"),
        Hover = Hex("#EDE6FD"),
        Accent = Hex("#7C3AED"),
        AccentFore = Hex("#6D28D9"),
        AccentHover = Hex("#6D28D9"),
        AccentText = Color.White,
        Good = Hex("#15803D"),
        Warn = Hex("#B45309"),
        Bad = Hex("#B91C1C"),
    };

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Back = Hex("#1C1C21"),
        Surface = Hex("#232329"),
        Raised = Hex("#2A2A31"),
        Border = Hex("#3A3A44"),
        Text = Hex("#E7E7EC"),
        Muted = Hex("#8E8E9B"),
        Hover = Hex("#3B2F5C"),
        Accent = Hex("#8B5CF6"),
        AccentFore = Hex("#B79CFF"),
        AccentHover = Hex("#7A4AEF"),
        AccentText = Color.White,
        Good = Hex("#4ADE80"),
        Warn = Hex("#FBBF24"),
        Bad = Hex("#F87171"),
    };
}

internal static class Theme
{
    public static Palette Current { get; private set; } = Palette.Light;

    /// <summary>Pick the palette without styling a window (used by the splash, which paints itself).</summary>
    public static void Use(bool dark) => Current = dark ? Palette.Dark : Palette.Light;

    /// <summary>Rounded corners on a borderless window (Windows 11; ignored on 10).</summary>
    public static void RoundCorners(Form f)
    {
        if (!f.IsHandleCreated) return;
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { }
    }

    /// <summary>Switch the whole window (plus any menus that aren't in its control tree) to light or dark.</summary>
    public static void Apply(Form form, bool dark, params ToolStrip[] extraStrips)
    {
        Current = dark ? Palette.Dark : Palette.Light;
        var renderer = new ThemedRenderer(Current);

        form.SuspendLayout();
        form.BackColor = Current.Back;
        form.ForeColor = Current.Text;
        foreach (Control c in form.Controls) ApplyTo(c, renderer);
        foreach (var s in extraStrips) ApplyStrip(s, renderer);
        form.ResumeLayout();

        ApplyTitleBar(form);
        form.Invalidate(true);
    }

    private static void ApplyTo(Control c, ThemedRenderer renderer)
    {
        var p = Current;
        switch (c)
        {
            case AxHost:
                return; // the Remote Desktop control draws itself; leave it alone
            case ToolStrip ts:
                ApplyStrip(ts, renderer);
                return;
            case ListView lv:
                lv.BackColor = p.Surface;
                lv.ForeColor = p.Text;
                StyleScrollbars(lv);
                lv.Invalidate();
                return;
            case TextBox tb:
                tb.BackColor = p.Surface;
                tb.ForeColor = p.Text;
                tb.BorderStyle = p.IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                return;
            case Button b:
                bool primary = (b.Tag as string) == "primary";
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = primary ? p.Accent : p.Surface;
                b.ForeColor = primary ? p.AccentText : p.Text;
                b.FlatAppearance.BorderColor = primary ? p.Accent : p.Border;
                b.FlatAppearance.MouseOverBackColor = primary ? p.AccentHover : p.Hover;
                b.FlatAppearance.MouseDownBackColor = primary ? p.AccentHover : p.Hover;
                return;
            case ComboBox combo:
                combo.BackColor = p.Surface;
                combo.ForeColor = p.Text;
                combo.FlatStyle = p.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                return;
            case LinkLabel link:
                link.BackColor = p.Back;
                link.ForeColor = p.Text;
                link.LinkColor = p.AccentFore;
                link.ActiveLinkColor = p.Accent;
                link.VisitedLinkColor = p.AccentFore;
                return;
            case Label l when (l.Tag as string) == "muted":
                l.BackColor = p.Back;
                l.ForeColor = p.Muted;
                return;
            case Label l when (l.Tag as string) == "accent":
                l.BackColor = p.Back;
                l.ForeColor = p.AccentFore;
                return;
            case Panel side when (side.Tag as string) == "sidebar":
                // settings navigation: a slightly raised strip, everything in it on the same background
                side.BackColor = p.Raised;
                foreach (Control child in side.Controls) { child.BackColor = p.Raised; child.ForeColor = p.Text; child.Invalidate(); }
                return;
            case ThemedCheckBox tcb:
                tcb.BackColor = p.Back;
                tcb.ForeColor = p.Text;
                tcb.Invalidate();
                return;
            case CheckBox cb:
                cb.BackColor = p.Back;
                cb.ForeColor = p.Text;
                cb.FlatStyle = p.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                cb.FlatAppearance.BorderColor = p.Muted;
                return;
            case SplitContainer sc:
                sc.BackColor = p.Border; // the splitter bar itself
                break;
            default:
                c.BackColor = p.Back;
                c.ForeColor = p.Text;
                break;
        }
        foreach (Control child in c.Controls) ApplyTo(child, renderer);
    }

    private static void ApplyStrip(ToolStrip s, ThemedRenderer renderer)
    {
        s.Renderer = renderer;
        s.BackColor = s is ToolStripDropDown ? Current.Surface : Current.Raised;
        s.ForeColor = Current.Text;
        foreach (ToolStripItem item in s.Items) ApplyItem(item, renderer);
        s.Invalidate();
    }

    private static void ApplyItem(ToolStripItem item, ThemedRenderer renderer)
    {
        var p = Current;
        item.ForeColor = p.Text;
        switch (item)
        {
            case ToolStripTextBox tb:
                tb.BackColor = p.Surface;
                tb.BorderStyle = p.IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                break;
            case ToolStripComboBox cb:
                cb.BackColor = p.Surface;
                cb.FlatStyle = p.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                break;
            case ToolStripProgressBar pb:
                ColorProgressBar(pb.ProgressBar);
                break;
            case ToolStripDropDownItem dd when dd.HasDropDownItems:
                ApplyStrip(dd.DropDown, renderer);
                break;
        }
    }

    /// <summary>Style a menu that's built after the theme was applied (e.g. the column chooser).</summary>
    public static void ApplyMenu(ToolStrip menu) => ApplyStrip(menu, new ThemedRenderer(Current));

    /// <summary>Dark (or normal) scrollbars on a list. Uses the theme Windows Explorer uses.</summary>
    public static void StyleScrollbars(Control c)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    /// <summary>Dark title bar (Windows 10 2004+ / 11) and a purple window border (Windows 11).</summary>
    public static void ApplyTitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        try
        {
            int dark = Current.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

            int border = ColorRef(Current.Accent);
            DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int)); // ignored before Windows 11
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>Purple marquee instead of the system green one.</summary>
    private static void ColorProgressBar(ProgressBar bar)
    {
        if (!bar.IsHandleCreated)
        {
            bar.HandleCreated += OnBarHandleCreated;
            return;
        }
        try
        {
            IntPtr h = bar.Handle;
            SetWindowTheme(h, "", ""); // colours are only honoured with the classic style
            SendMessage(h, PBM_SETBARCOLOR, IntPtr.Zero, (IntPtr)ColorRef(Current.Accent));
            SendMessage(h, PBM_SETBKCOLOR, IntPtr.Zero, (IntPtr)ColorRef(Current.Raised));
        }
        catch { }
    }

    private static void OnBarHandleCreated(object sender, EventArgs e)
    {
        var bar = (ProgressBar)sender;
        bar.HandleCreated -= OnBarHandleCreated;
        ColorProgressBar(bar);
    }

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int PBM_SETBARCOLOR = 0x0409;
    private const int PBM_SETBKCOLOR = 0x2001;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Toolbar icons from the Windows icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on 10).</summary>
internal static class Icons
{
    public const string List = "\uE8FD";     // bulleted list
    public const string Refresh = "\uE72C";  // circular arrow
    public const string Connect = "\uE703";  // plug / connect
    public const string Start = "\uE768";    // play
    public const string Stop = "\uE71A";     // stop square
    public const string Reboot = "\uE895";   // two arrows in a circle
    public const string Settings = "\uE713"; // gear
    public const string Add = "\uE710";      // plus

    /// <summary>A glyph drawn into a transparent square bitmap, for ToolStrip items that show an icon and text.</summary>
    public static Bitmap Render(string glyph, Color color, int px)
    {
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(Font.FontFamily, px * 0.8f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, px, px), fmt);
        return bmp;
    }

    private static Font _font;
    public static Font Font => _font ??= Create();

    private static Font Create()
    {
        // Windows silently substitutes a missing font, so ask for the Windows 11 font and check what we got.
        // (Much faster than listing every installed font.)
        var fluent = new Font("Segoe Fluent Icons", 12F);
        if (fluent.Name == "Segoe Fluent Icons") return fluent;
        fluent.Dispose();
        return new Font("Segoe MDL2 Assets", 12F);
    }
}

/// <summary>Colours for the toolbar, status bar and right-click menus.</summary>
internal sealed class ThemedColors : ProfessionalColorTable
{
    private readonly Palette _p;
    public ThemedColors(Palette p) { _p = p; UseSystemColors = false; }

    public override Color MenuStripGradientBegin => _p.Raised;
    public override Color MenuStripGradientEnd => _p.Raised;
    public override Color ToolStripGradientBegin => _p.Raised;
    public override Color ToolStripGradientMiddle => _p.Raised;
    public override Color ToolStripGradientEnd => _p.Raised;
    public override Color ToolStripBorder => _p.Raised;
    public override Color ToolStripDropDownBackground => _p.Surface;
    public override Color StatusStripGradientBegin => _p.Raised;
    public override Color StatusStripGradientEnd => _p.Raised;
    public override Color OverflowButtonGradientBegin => _p.Raised;
    public override Color OverflowButtonGradientMiddle => _p.Raised;
    public override Color OverflowButtonGradientEnd => _p.Raised;
    public override Color GripDark => _p.Border;
    public override Color GripLight => _p.Raised;
    public override Color SeparatorDark => _p.Border;
    public override Color SeparatorLight => _p.Raised;

    public override Color ImageMarginGradientBegin => _p.Surface;
    public override Color ImageMarginGradientMiddle => _p.Surface;
    public override Color ImageMarginGradientEnd => _p.Surface;
    public override Color MenuBorder => _p.Border;
    public override Color MenuItemBorder => _p.Accent;
    public override Color MenuItemSelected => _p.Hover;
    public override Color MenuItemSelectedGradientBegin => _p.Hover;
    public override Color MenuItemSelectedGradientEnd => _p.Hover;
    public override Color MenuItemPressedGradientBegin => _p.Hover;
    public override Color MenuItemPressedGradientMiddle => _p.Hover;
    public override Color MenuItemPressedGradientEnd => _p.Hover;

    public override Color ButtonSelectedHighlight => _p.Hover;
    public override Color ButtonSelectedGradientBegin => _p.Hover;
    public override Color ButtonSelectedGradientMiddle => _p.Hover;
    public override Color ButtonSelectedGradientEnd => _p.Hover;
    public override Color ButtonSelectedBorder => _p.Accent;
    public override Color ButtonPressedHighlight => _p.Accent;
    public override Color ButtonPressedGradientBegin => _p.Accent;
    public override Color ButtonPressedGradientMiddle => _p.Accent;
    public override Color ButtonPressedGradientEnd => _p.Accent;
    public override Color ButtonPressedBorder => _p.Accent;
    public override Color ButtonCheckedHighlight => _p.Accent;
    public override Color ButtonCheckedHighlightBorder => _p.Accent;
    public override Color ButtonCheckedGradientBegin => _p.Accent;
    public override Color ButtonCheckedGradientMiddle => _p.Accent;
    public override Color ButtonCheckedGradientEnd => _p.Accent;
    public override Color CheckBackground => _p.Accent;
    public override Color CheckSelectedBackground => _p.Accent;
    public override Color CheckPressedBackground => _p.Accent;
}

internal sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    private readonly Palette _p;
    public ThemedRenderer(Palette p) : base(new ThemedColors(p)) { _p = p; RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.Tag is Tone tone && e.Item.Enabled)
            e.TextColor = tone switch { Tone.Good => _p.Good, Tone.Warn => _p.Warn, _ => _p.Bad };
        else if (e.Item is ToolStripButton { Checked: true } || (e.Item.Pressed && !e.Item.IsOnDropDown))
            e.TextColor = _p.AccentText;
        else
            e.TextColor = e.Item.Enabled ? _p.Text : _p.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Enabled: false } ? _p.Muted : _p.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_p.Border);
        var r = e.AffectedBounds;
        if (e.ToolStrip is ToolStripDropDown)
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        else if (e.ToolStrip is StatusStrip)
            e.Graphics.DrawLine(pen, r.Left, r.Top, r.Right, r.Top);
        else
            e.Graphics.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
    }
}

/// <summary>Server list that draws its own rows and headers so it can follow the theme.</summary>
internal sealed class ThemedListView : ListView
{
    public int SortColumn = -1;
    public bool SortDescending;
    /// <summary>Optional text colour for a cell (e.g. the Status column); null keeps the normal colour.</summary>
    public Func<ListViewItem, int, Color?> CellColor;

    private const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    public ThemedListView()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
    }

    /// <summary>Stretch the last column to the edge so no unpainted header area is left over.</summary>
    public void FillLastColumn()
    {
        if (View == View.Details && Columns.Count > 0) Columns[Columns.Count - 1].Width = -2;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleScrollbars(this);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FillLastColumn();
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var p = Theme.Current;
        using (var b = new SolidBrush(p.Raised)) e.Graphics.FillRectangle(b, e.Bounds);

        bool sorted = e.ColumnIndex == SortColumn;
        string text = e.Header.Text + (sorted ? (SortDescending ? "  ▼" : "  ▲") : "");
        TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -6, 0),
            sorted ? p.AccentFore : p.Text, Flags);

        using var pen = new Pen(p.Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) { /* drawn per cell below */ }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var p = Theme.Current;
        var bounds = e.Bounds;
        if (e.ColumnIndex == 0) bounds.Width = Columns[0].Width; // Windows reports the whole row for cell 0

        bool selected = e.Item.Selected;
        bool offline = e.Item.Tag is Instance { Online: false };

        using (var b = new SolidBrush(selected ? p.Accent : p.Surface)) e.Graphics.FillRectangle(b, bounds);
        var fore = selected ? p.AccentText : CellColor?.Invoke(e.Item, e.ColumnIndex) ?? (offline ? p.Muted : p.Text);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, Rectangle.Inflate(bounds, -6, 0), fore, Flags);

        if (!selected)
        {
            using var pen = new Pen(p.Border);
            e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }
    }
}

/// <summary>Tab strip for the remote sessions, painted in the theme with a purple bar on the active tab and a close button.</summary>
internal sealed class ThemedTabControl : TabControl
{
    /// <summary>Raised when the × on a tab is clicked.</summary>
    public event Action<TabPage> CloseRequested;

    private const int CloseSize = 16;
    private const int WM_LBUTTONDOWN = 0x0201;
    private int _hoverClose = -1;

    public ThemedTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Point(22, 6); // room for the × on the right
    }

    private Rectangle CloseRect(int i)
    {
        var r = GetTabRect(i);
        return new Rectangle(r.Right - CloseSize - 7, r.Top + (r.Height - CloseSize) / 2 + 1, CloseSize, CloseSize);
    }

    private int CloseHit(Point pt)
    {
        for (int i = 0; i < TabCount; i++)
            if (CloseRect(i).Contains(pt)) return i;
        return -1;
    }

    protected override void WndProc(ref Message m)
    {
        // Catch the click before Windows selects the tab, so closing a background tab doesn't switch to it first.
        if (m.Msg == WM_LBUTTONDOWN)
        {
            long lp = m.LParam.ToInt64();
            var pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            int i = CloseHit(pt);
            if (i >= 0)
            {
                var page = TabPages[i];
                BeginInvoke(() => CloseRequested?.Invoke(page));
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = CloseHit(e.Location);
        if (h != _hoverClose) { _hoverClose = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverClose != -1) { _hoverClose = -1; Invalidate(); }
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        _hoverClose = -1;
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Back);
        if (TabCount == 0) return;

        int stripBottom = GetTabRect(0).Bottom;
        using (var b = new SolidBrush(p.Raised)) g.FillRectangle(b, 0, 0, Width, stripBottom);

        using var border = new Pen(p.Border);
        using var back = new SolidBrush(p.Back);
        using var accent = new SolidBrush(p.Accent);

        Rectangle selRect = Rectangle.Empty;
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool sel = i == SelectedIndex;
            if (sel)
            {
                selRect = r;
                g.FillRectangle(back, r);
                g.FillRectangle(accent, r.Left, r.Top, r.Width, 3);
            }
            else
            {
                g.DrawLine(border, r.Right - 1, r.Top + 6, r.Right - 1, r.Bottom - 6);
            }
            var textRect = new Rectangle(r.Left + 12, r.Top, r.Width - 12 - CloseSize - 12, r.Height);
            TextRenderer.DrawText(g, TabPages[i].Text, Font, textRect, sel ? p.Text : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            DrawClose(g, CloseRect(i), sel, i == _hoverClose, p);
        }

        // line under the strip, open under the active tab so it reads as attached to its page
        if (selRect.IsEmpty)
            g.DrawLine(border, 0, stripBottom, Width, stripBottom);
        else
        {
            g.DrawLine(border, 0, stripBottom, selRect.Left, stripBottom);
            g.DrawLine(border, selRect.Right, stripBottom, Width, stripBottom);
        }
    }

    private static void DrawClose(Graphics g, Rectangle r, bool selected, bool hover, Palette p)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (hover)
        {
            using var hb = new SolidBrush(p.Accent);
            g.FillEllipse(hb, r);
        }
        using var pen = new Pen(hover ? p.AccentText : selected ? p.Text : p.Muted, 1.6f);
        int inset = 5;
        g.DrawLine(pen, r.Left + inset, r.Top + inset, r.Right - inset, r.Bottom - inset);
        g.DrawLine(pen, r.Right - inset, r.Top + inset, r.Left + inset, r.Bottom - inset);
        g.SmoothingMode = old;
    }
}

/// <summary>
/// Checkbox drawn in the theme so ticked and unticked are obvious in both looks:
/// ticked = solid purple box with a white tick, unticked = empty box with a clear outline, purple outline on hover.
/// </summary>
internal sealed class ThemedCheckBox : CheckBox
{
    private bool _hover;

    public ThemedCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    private int BoxSize => (int)Math.Round(16 * (DeviceDpi > 0 ? DeviceDpi : 96) / 96f);
    private int Gap => BoxSize / 2;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(Padding.Horizontal + 2 + BoxSize + Gap + text.Width + 2,
                        Math.Max(BoxSize + 4, text.Height + 4) + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);

        int size = BoxSize;
        var box = new Rectangle(Padding.Left + 1, (ClientSize.Height - size) / 2, size, size);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using (var path = Rounded(box, Math.Max(3, size / 5)))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(!Enabled ? p.Muted : _hover ? p.AccentHover : p.Accent);
                g.FillPath(fill, path);

                // white tick
                using var tick = new Pen(p.AccentText, Math.Max(2f, size / 8f))
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                g.DrawLines(tick, new[]
                {
                    new PointF(box.Left + size * 0.24f, box.Top + size * 0.52f),
                    new PointF(box.Left + size * 0.43f, box.Top + size * 0.70f),
                    new PointF(box.Left + size * 0.77f, box.Top + size * 0.32f),
                });
            }
            else
            {
                using var fill = new SolidBrush(p.Surface);
                g.FillPath(fill, path);
                // outline bright enough to see on the dark background
                var outline = !Enabled ? p.Border : _hover ? p.Accent : p.IsDark ? Color.FromArgb(0xA8, 0xA8, 0xB6) : p.Muted;
                using var pen = new Pen(outline, 1.6f);
                g.DrawPath(pen, path);
            }
        }

        if (Focused && ShowFocusCues)
        {
            var ring = Rectangle.Inflate(box, 3, 3);
            using var ringPath = Rounded(ring, Math.Max(4, size / 4));
            using var pen = new Pen(Color.FromArgb(160, p.Accent), 1.5f);
            g.DrawPath(pen, ringPath);
        }
        g.SmoothingMode = oldMode;

        var textRect = new Rectangle(box.Right + Gap, 0, Math.Max(0, ClientSize.Width - box.Right - Gap - Padding.Right),
                                     ClientSize.Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : p.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
