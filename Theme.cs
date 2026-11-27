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

    /// <summary>The original purple. With it, the palettes are exactly the hand-tuned ones below.</summary>
    public static readonly Color DefaultAccent = Hex("#7C3AED");

    private static readonly Color LightBack = Hex("#F0F0F2"), DarkBack = Hex("#1C1C21");

    /// <summary>The look for light or dark mode with the given accent colour.</summary>
    public static Palette Create(bool dark, Color accent)
    {
        accent = Color.FromArgb(255, accent);
        bool purple = accent.ToArgb() == DefaultAccent.ToArgb();
        if (!dark)
        {
            return new Palette
            {
                IsDark = false,
                Back = LightBack, Surface = Hex("#FFFFFF"), Raised = Hex("#F7F7F9"), Border = Hex("#D6D6DD"),
                Text = Hex("#1B1B1F"), Muted = Hex("#7A7A86"),
                Accent = accent,
                Hover = purple ? Hex("#EDE6FD") : ColorMath.Mix(accent, Color.White, 0.86),
                AccentFore = purple ? Hex("#6D28D9") : ColorMath.Readable(accent, Color.White, towardsLight: false),
                AccentHover = purple ? Hex("#6D28D9") : ColorMath.Mix(accent, Color.Black, 0.14),
                AccentText = ColorMath.TextOn(accent),
                Good = Hex("#15803D"), Warn = Hex("#B45309"), Bad = Hex("#B91C1C"),
            };
        }
        // dark mode: a very dark accent is lifted a little so it still stands out on the dark background
        var fill = purple ? Hex("#8B5CF6") : ColorMath.Luminance(accent) < 0.12 ? ColorMath.Mix(accent, Color.White, 0.22) : accent;
        return new Palette
        {
            IsDark = true,
            Back = DarkBack, Surface = Hex("#232329"), Raised = Hex("#2A2A31"), Border = Hex("#3A3A44"),
            Text = Hex("#E7E7EC"), Muted = Hex("#8E8E9B"),
            Accent = fill,
            Hover = purple ? Hex("#3B2F5C") : ColorMath.Mix(fill, DarkBack, 0.72),
            AccentFore = purple ? Hex("#B79CFF") : ColorMath.Readable(fill, DarkBack, towardsLight: true),
            AccentHover = purple ? Hex("#7A4AEF") : ColorMath.Mix(fill, Color.Black, 0.12),
            AccentText = ColorMath.TextOn(fill),
            Good = Hex("#4ADE80"), Warn = Hex("#FBBF24"), Bad = Hex("#F87171"),
        };
    }

    public static readonly Palette Light = Create(false, DefaultAccent);
    public static readonly Palette Dark = Create(true, DefaultAccent);
}

/// <summary>Colour helpers for deriving an accent's shades with readable contrast (WCAG formulas).</summary>
internal static class ColorMath
{
    public static Color Mix(Color a, Color b, double towardsB)
    {
        double t = Math.Clamp(towardsB, 0, 1);
        return Color.FromArgb(255,
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static double Luminance(Color c)
    {
        static double Ch(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The accent, darkened (or lightened) step by step until it reads as text on the background (4.5:1).</summary>
    public static Color Readable(Color accent, Color background, bool towardsLight)
    {
        var c = accent;
        for (int i = 0; i < 12 && Contrast(c, background) < 4.5; i++)
            c = Mix(c, towardsLight ? Color.White : Color.Black, 0.14);
        return c;
    }

    /// <summary>White or near-black, whichever is easier to read on the colour (e.g. dark text on amber).</summary>
    public static Color TextOn(Color fill) =>
        Contrast(Color.White, fill) >= Contrast(Color.FromArgb(17, 17, 20), fill) ? Color.White : Color.FromArgb(17, 17, 20);

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

internal static class Theme
{
    public static Palette Current { get; private set; } = Palette.Light;

    // ---- customisation (Settings > Appearance), set from the settings by Configure()
    public static Color Accent { get; private set; } = Palette.DefaultAccent;
    public const string DefaultUiFont = "Segoe UI";
    public static string UiFont { get; private set; } = DefaultUiFont;
    public const string DefaultTerminalFont = "Cascadia Mono";
    public static string TerminalFont { get; private set; } = DefaultTerminalFont;
    public static int TerminalFontSize { get; private set; } = 14;
    /// <summary>The terminal colour scheme, or null to match the app's look.</summary>
    public static TerminalScheme TerminalScheme { get; private set; }

    /// <summary>Accent presets, in the order Settings shows them. The first is the default.</summary>
    public static readonly (string Name, Color Color)[] AccentPresets =
    {
        ("Purple", Palette.DefaultAccent),
        ("Blue", ColorTranslator.FromHtml("#2563EB")),
        ("Teal", ColorTranslator.FromHtml("#0D9488")),
        ("Green", ColorTranslator.FromHtml("#16A34A")),
        ("Amber", ColorTranslator.FromHtml("#D97706")),
        ("Red", ColorTranslator.FromHtml("#DC2626")),
        ("Pink", ColorTranslator.FromHtml("#DB2777")),
        ("Graphite", ColorTranslator.FromHtml("#475569")),
    };

    /// <summary>Take the accent and fonts from the settings ("" = default, "#RRGGBB", or "Windows").</summary>
    public static void Configure(AppSettings s)
    {
        Accent = ResolveAccent(s.AccentColor);
        UiFont = string.IsNullOrWhiteSpace(s.UiFont) ? DefaultUiFont : s.UiFont.Trim();
        TerminalFont = string.IsNullOrWhiteSpace(s.TerminalFont) ? DefaultTerminalFont : s.TerminalFont.Trim();
        TerminalFontSize = s.TerminalFontSize is >= 8 and <= 32 ? s.TerminalFontSize : 14;
        TerminalScheme = TerminalSchemes.Find(s.TerminalScheme);
    }

    public static Color ResolveAccent(string setting)
    {
        if (string.Equals(setting, "Windows", StringComparison.OrdinalIgnoreCase)) return WindowsAccent() ?? Palette.DefaultAccent;
        if (!string.IsNullOrWhiteSpace(setting))
        {
            try { return Color.FromArgb(255, ColorTranslator.FromHtml(setting.Trim())); } catch { }
        }
        return Palette.DefaultAccent;
    }

    /// <summary>Windows' "apps" light/dark setting (Settings > Personalisation > Colours).</summary>
    public static bool WindowsIsDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>The Windows accent colour, or null if it can't be read.</summary>
    public static Color? WindowsAccent()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr) // 0xAABBGGRR
                return Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            if (k?.GetValue("ColorizationColor") is int argb) // 0xAARRGGBB
                return Color.FromArgb(255, Color.FromArgb(argb));
        }
        catch { }
        return null;
    }

    // ---- app font: every control or item using Segoe UI is switched to the chosen family (sizes kept)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Font> _originalFonts = new();

    /// <summary>The font to use for something whose font was originally <paramref name="current"/>.</summary>
    private static Font UiFontFor(object owner, Font current)
    {
        if (current == null) return null;
        var original = _originalFonts.GetValue(owner, _ => current);
        string family = original.FontFamily.Name;
        // only the app's own text font; icon fonts (Segoe MDL2 / Fluent Icons) and monospace fonts stay as they are
        if (!family.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase)) return current;
        if (UiFont == DefaultUiFont) return original;
        var style = original.Style;
        if (family.Contains("Semibold", StringComparison.OrdinalIgnoreCase) || family.Contains("Bold", StringComparison.OrdinalIgnoreCase))
            style |= FontStyle.Bold; // other families have no separate "Semibold": use bold
        return CreateFont(UiFont, original.Size, style, original.Unit);
    }

    /// <summary>
    /// A font by family name: from the fonts folder (FontLoader) if it's there, else an installed font. If the
    /// family has no such style (e.g. no bold file in the folder), its regular style is used instead.
    /// </summary>
    public static Font CreateFont(string family, float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        if (FontLoader.TryGetFamily(family, out var loaded))
        {
            bool wantedBold = style.HasFlag(FontStyle.Bold);
            if (!loaded.IsStyleAvailable(style))
                style = loaded.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular
                      : loaded.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Italic;
            var font = new Font(loaded, size, style, unit);
            // no bold file for this family: remember it was meant to be bold, so menus can draw a fake bold
            if (wantedBold && !style.HasFlag(FontStyle.Bold)) FauxBold.AddOrUpdate(font, null);
            return font;
        }
        return new Font(family, size, style, unit);
    }

    // fonts that stand in for a bold the family doesn't have (see CreateFont, ThemedRenderer.OnRenderItemText)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Font, object> FauxBold = new();

    /// <summary>The font was asked for in bold but its family has no bold style: draw it thicker by hand (fake bold).</summary>
    public static bool IsFauxBold(Font font) => font != null && FauxBold.TryGetValue(font, out _);

    /// <summary>Apply the app font to a control created after the theme was applied.</summary>
    public static void ApplyFont(Control c)
    {
        if (c is AxHost) return;
        var f = UiFontFor(c, c.Font);
        if (f != null && !ReferenceEquals(f, c.Font)) c.Font = f;
        AttachFauxBold(c);
    }

    // ---- fake bold for labels and flat buttons (headings, the Server window's selected page ...)

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> FauxBoldPainted = new();

    /// <summary>
    /// Labels and flat buttons repaint their own text when their font stands in for a missing bold (checked at paint
    /// time, so a font set later, like the Server window's selected page, is covered too).
    /// </summary>
    private static void AttachFauxBold(Control c)
    {
        if (c is not (Label or Button { FlatStyle: FlatStyle.Flat }) || FauxBoldPainted.TryGetValue(c, out _)) return;
        FauxBoldPainted.Add(c, null);
        c.Paint += PaintFauxBold;
    }

    /// <summary>
    /// The control has already drawn its text; for a fake bold, paint it again ourselves: the background, then the text
    /// twice, the second a pixel to the right (both passes ours, so they line up exactly).
    /// </summary>
    private static void PaintFauxBold(object sender, PaintEventArgs e)
    {
        var c = (Control)sender;
        if (!IsFauxBold(c.Font) || string.IsNullOrEmpty(c.Text)) return;
        var back = EffectiveBack(c);
        if (c is Button b && !b.FlatAppearance.MouseOverBackColor.IsEmpty && b.ClientRectangle.Contains(b.PointToClient(Cursor.Position)))
            back = b.FlatAppearance.MouseOverBackColor;
        using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, c.ClientRectangle);

        var align = c is Label l ? l.TextAlign : ((Button)c).TextAlign;
        bool wraps = c is not Label { AutoSize: true, MaximumSize.Width: 0 };
        var flags = AlignFlags(align) | (wraps ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine) |
                    (c is Label { UseMnemonic: false } ? TextFormatFlags.NoPrefix : TextFormatFlags.HidePrefix);
        var area = new Rectangle(c.Padding.Left, c.Padding.Top,
            Math.Max(0, c.ClientSize.Width - c.Padding.Horizontal), Math.Max(0, c.ClientSize.Height - c.Padding.Vertical));
        var color = c.Enabled ? c.ForeColor : Current.Muted;
        TextRenderer.DrawText(e.Graphics, c.Text, c.Font, area, color, flags);
        area.Offset(1, 0);
        TextRenderer.DrawText(e.Graphics, c.Text, c.Font, area, color, flags);
    }

    /// <summary>The colour actually behind a control: its own, or the first solid one among its parents.</summary>
    private static Color EffectiveBack(Control c)
    {
        for (var x = c; x != null; x = x.Parent)
            if (x.BackColor.A == 255) return x.BackColor;
        return Current.Back;
    }

    private static TextFormatFlags AlignFlags(ContentAlignment a)
    {
        var h = a is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? TextFormatFlags.HorizontalCenter
              : a is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight ? TextFormatFlags.Right
              : TextFormatFlags.Left;
        var v = a is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? TextFormatFlags.VerticalCenter
              : a is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight ? TextFormatFlags.Bottom
              : TextFormatFlags.Top;
        return h | v;
    }

    /// <summary>Pick the palette without styling a window (used by the splash, which paints itself).</summary>
    public static void Use(bool dark) => Current = Palette.Create(dark, Accent);

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
    /// <summary>
    /// Per-monitor scaling (Program: HighDpiMode.PerMonitorV2): a window keeps the sizes it was built with on the screen
    /// it opens on (as before), and WinForms rescales it, fonts and controls, when it moves to a screen with a different
    /// scaling. Done once per window, from Apply and ApplyTitleBar (every window passes through one of them).
    /// </summary>
    public static void EnableDpiRescaling(Form form)
    {
        if (form == null || form.AutoScaleMode == AutoScaleMode.Dpi && form.AutoScaleDimensions != SizeF.Empty) return;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = form.CurrentAutoScaleDimensions;   // "built for this screen": no rescaling now, only on moves
    }

    public static void Apply(Form form, bool dark, params ToolStrip[] extraStrips)
    {
        EnableDpiRescaling(form);
        Current = Palette.Create(dark, Accent);
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
        if (c is not AxHost) ApplyFont(c);
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
        var stripFont = UiFontFor(s, s.Font);
        if (stripFont != null && !ReferenceEquals(stripFont, s.Font)) s.Font = stripFont;
        s.Renderer = renderer;
        s.BackColor = s is ToolStripDropDown ? Current.Surface : Current.Raised;
        s.ForeColor = Current.Text;
        foreach (ToolStripItem item in s.Items) ApplyItem(item, renderer);
        s.Invalidate();
    }

    private static void ApplyItem(ToolStripItem item, ThemedRenderer renderer)
    {
        var p = Current;
        var itemFont = UiFontFor(item, item.Font);
        if (itemFont != null && !ReferenceEquals(itemFont, item.Font)) item.Font = itemFont;
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
    private const int EM_SETMARGINS = 0xD3, EC_RIGHTMARGIN = 0x2;   // sent with Theme's existing SendMessage

    /// <summary>
    /// An eye button inside the right edge of a password box: click to show the password (to check for typos) and again
    /// to hide it. The text keeps clear of the button, and the button follows the box's colours.
    /// </summary>
    public static void AddPasswordReveal(TextBox box)
    {
        if (box.Controls.OfType<Label>().Any(l => Equals(l.Tag, "reveal"))) return;
        bool system = box.UseSystemPasswordChar;
        char mask = box.PasswordChar;
        bool shown = false;
        var tip = new ToolTip();
        var eye = new Label
        {
            Tag = "reveal", Text = Icons.Show, Font = new Font(Icons.Font.FontFamily, 10F),
            Width = box.LogicalToDeviceUnits(22), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            BackColor = box.BackColor, ForeColor = Current.Muted, UseMnemonic = false,
        };
        tip.SetToolTip(eye, "Show password");
        eye.Click += (_, _) =>
        {
            shown = !shown;
            if (system) box.UseSystemPasswordChar = !shown;
            else box.PasswordChar = shown ? '\0' : mask;
            eye.Text = shown ? Icons.Hide : Icons.Show;
            tip.SetToolTip(eye, shown ? "Hide password" : "Show password");
            box.Focus();
            box.SelectionStart = box.TextLength;
        };
        // the theme recolours labels for the window: keep this one the box's colours
        box.BackColorChanged += (_, _) => eye.BackColor = box.BackColor;
        eye.BackColorChanged += (_, _) => { if (eye.BackColor != box.BackColor) eye.BackColor = box.BackColor; };
        eye.ForeColorChanged += (_, _) => { if (eye.ForeColor != Current.Muted) eye.ForeColor = Current.Muted; };
        box.Controls.Add(eye);

        // inset from the box's top, right and bottom edges, so its border is never covered (the dark theme's single line
        // and the light theme's 3D edge alike); kept there as the box resizes
        void Place()
        {
            int inset = box.LogicalToDeviceUnits(2);
            var area = box.ClientSize;
            eye.Bounds = new Rectangle(area.Width - eye.Width - inset, inset, eye.Width, Math.Max(1, area.Height - inset * 2));
        }

        // keep typed text clear of the button (and again whenever the box's window is recreated, e.g. on toggling)
        void Margin()
        {
            if (box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETMARGINS, (IntPtr)EC_RIGHTMARGIN, (IntPtr)((eye.Width + box.LogicalToDeviceUnits(4)) << 16));
        }
        box.ClientSizeChanged += (_, _) => Place();
        box.HandleCreated += (_, _) => { Place(); Margin(); };
        Place();
        Margin();
    }

    /// <summary>
    /// Open a menu at a screen point without it crossing onto another screen: if it would run past the edge of the
    /// screen it opens on, it opens leftwards (or upwards) from the point instead.
    /// </summary>
    public static void ShowMenuOnScreen(ToolStripDropDown menu, Point at)
    {
        var area = Screen.FromPoint(at).WorkingArea;
        var size = menu.GetPreferredSize(Size.Empty);
        int x = at.X + size.Width > area.Right ? Math.Max(area.Left, at.X - size.Width) : at.X;
        int y = at.Y + size.Height > area.Bottom ? Math.Max(area.Top, at.Y - size.Height) : at.Y;
        menu.Show(new Point(x, y));
    }

    public static void ApplyTitleBar(Form f)
    {
        EnableDpiRescaling(f);
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
    public const string SignIn = "\uE77B";   // person
    public const string SignOut = "\uE7E8";  // power
    public const string Terminal = "\uE756"; // command prompt
    public const string Show = "\uE890";     // eye (show password)
    public const string Hide = "\uED1A";     // crossed-out eye (hide password)
    public const string Undock = "\uE8A7";   // open in a new window (back to a tab)
    public const string HideDown = "\uE70D"; // chevron down
    public const string ShowUp = "\uE70E";   // chevron up
    public const string CloseX = "\uE711";   // ×
    public const string FullScreen = "\uE740";      // four corners out
    public const string ExitFullScreen = "\uE73F";  // four corners in (back to a window)
    public const string Pin = "\uE718";      // pushpin (keep on top)
    public const string Unpin = "\uE77A";    // crossed pushpin
    public const string Keyboard = "\uE765"; // keyboard (Ctrl+Alt+Del)

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
        if (Theme.IsFauxBold(e.TextFont))
        {
            // a fake bold for a font without one, the way browsers do it: the same text again, a pixel to the right
            var r = e.TextRectangle;
            e.TextRectangle = new Rectangle(r.X + 1, r.Y, r.Width, r.Height);
            base.OnRenderItemText(e);
            e.TextRectangle = r;
        }
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
    /// <summary>Raised when the + after the last tab is clicked (screen point to show a menu at).</summary>
    public event Action<Point> NewTabRequested;
    /// <summary>Raised after tabs were dragged into a new order.</summary>
    public event Action TabsReordered;

    // dragging a tab along the strip to reorder
    private int _dragIndex = -1;
    private Point _dragStart;
    private bool _dragging;

    /// <summary>Tag for tab pages that hold a local shell: they get a terminal icon before their name.</summary>
    public const string ShellTag = "shell";
    /// <summary>Put this in front of a shell tab's text, to make room for its icon.</summary>
    public const string IconSpace = "      ";

    // a shell tab's own program icon (ShellIcons); kept with the page, so it follows it between the main and docked strips
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabPage, Image> TabIcons = new();

    /// <summary>The icon a shell tab shows (its program's icon); without one, the generic terminal glyph.</summary>
    public static void SetTabIcon(TabPage page, Image icon)
    {
        if (page != null && icon != null) TabIcons.AddOrUpdate(page, icon);
    }

    /// <summary>
    /// Remote tabs: a small badge before the name (AWS for servers reached through Session Manager; RDP or SSH for
    /// saved connections). The page's Tag is BadgeTag(badge) and its text starts with BadgeRoom(badge).
    /// </summary>
    public const string BadgeTagPrefix = "badge:";
    public static string BadgeTag(string badge) => BadgeTagPrefix + badge;

    // sizes follow the screen's scaling (per-monitor DPI): pixels at 100%, scaled to the screen the strip is on
    private int Px(int pixelsAt100) => (int)Math.Round(pixelsAt100 * DeviceDpi / 96.0);
    private int BadgeHeight => Px(16);
    private int BadgeGap => Px(6);
    private int CloseSize => Px(16);

    // the badge text scales with the tab text (which WinForms rescales when the window changes screens)
    private Font _badgeFont;
    private Font BadgeFont
    {
        get
        {
            float size = Font.SizeInPoints * 7f / 9f;
            if (_badgeFont == null || Math.Abs(_badgeFont.SizeInPoints - size) > 0.01f)
            {
                _badgeFont?.Dispose();
                _badgeFont = new Font("Segoe UI", size, FontStyle.Bold);
            }
            return _badgeFont;
        }
    }

    private int BadgeWidth(string badge) =>
        TextRenderer.MeasureText(badge, BadgeFont, Size.Empty, TextFormatFlags.NoPadding).Width + Px(10);

    /// <summary>
    /// A badge pill (AWS / RDP / SSH / VNC) in its fixed colours, softened when not selected. Shared by the tab strips and
    /// the popped-out window's toolbar, so the two look the same.
    /// </summary>
    public static void DrawBadge(Graphics g, string badge, Font font, Rectangle pill, bool selected, Palette p)
    {
        // fixed colours: AWS in AWS's orange, RDP in indigo (#5B5EC5), SSH in magenta (#944792), VNC in teal
        var (fill, fore) = badge switch
        {
            "AWS" => (Color.FromArgb(0xFF, 0x99, 0x00), Color.FromArgb(0x23, 0x2F, 0x3E)),
            "RDP" => (Color.FromArgb(0x5B, 0x5E, 0xC5), Color.White),
            "SSH" => (Color.FromArgb(0x94, 0x47, 0x92), Color.White),
            "VNC" => (Color.FromArgb(0x14, 0xB8, 0xA6), Color.FromArgb(0x10, 0x2A, 0x27)),
            _ => (p.Accent, p.AccentText),
        };
        if (!selected) { fill = Blend(fill, p.Back, 0.35); fore = Blend(fore, fill, 0.15); }
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        using (var brush = new SolidBrush(fill))
        {
            int d = 8;   // corner diameter
            path.AddArc(pill.Left, pill.Top, d, d, 180, 90);
            path.AddArc(pill.Right - d, pill.Top, d, d, 270, 90);
            path.AddArc(pill.Right - d, pill.Bottom - d, d, d, 0, 90);
            path.AddArc(pill.Left, pill.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
        g.SmoothingMode = oldMode;
        TextRenderer.DrawText(g, badge, font, pill, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    /// <summary>Spaces in front of a tab's text that leave room for its badge, measured in this strip's font.</summary>
    public string BadgeRoom(string badge)
    {
        int space = Math.Max(1, TextRenderer.MeasureText("x x", Font).Width - TextRenderer.MeasureText("xx", Font).Width);
        int need = BadgeWidth(badge) + BadgeGap;
        return new string(' ', (need + space - 1) / space);
    }

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private const int PlusSize = 22;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1, HTCLIENT = 1;
    private int _hoverClose = -1;
    private bool _hoverPlus;

    // ---- the + is a real (hidden) last tab: Windows lays it out like any tab, so it's never over a tab, scrolls with
    // them, and is fully visible at the end of the strip. It only exists while there are real tabs, is never selected,
    // and isn't counted: TabCount is the real tabs (base.TabCount includes the +).

    private readonly TabPage _plusPage = new(" ");
    private bool _adjusting;

    /// <summary>The real tabs (the + slot at the end isn't one).</summary>
    public new int TabCount => base.TabCount - (TabPages.Contains(_plusPage) ? 1 : 0);

    private int PlusIndex => TabPages.IndexOf(_plusPage);

    /// <summary>The + circle, in the + slot (empty when there's no + or it's scrolled out of view).</summary>
    private Rectangle PlusRect()
    {
        int index = PlusIndex;
        if (index < 0) return Rectangle.Empty;
        var slot = GetTabRect(index);
        var r = new Rectangle(slot.Left + Px(6), slot.Top + (slot.Height - PlusSize) / 2 + 1, PlusSize, PlusSize);
        var arrows = ScrollArrowsRect();
        if (r.Right <= 0 || r.Left >= ClientSize.Width || (!arrows.IsEmpty && r.Right > arrows.Left)) return Rectangle.Empty;
        return r;
    }

    /// <summary>The + slot itself (clicks anywhere in it are handled here and never select it).</summary>
    private Rectangle PlusSlot() => PlusIndex is var i && i >= 0 ? GetTabRect(i) : Rectangle.Empty;

    /// <summary>The + slot: added with the first real tab, kept last, removed with the last real tab.</summary>
    private void KeepPlusLast()
    {
        if (_adjusting || IsDisposed) return;
        _adjusting = true;
        try
        {
            bool has = TabPages.Contains(_plusPage);
            if (TabCount == 0) { if (has) TabPages.Remove(_plusPage); return; }
            if (!has) { TabPages.Add(_plusPage); return; }
            int at = PlusIndex, last = base.TabCount - 1;
            if (at == last) return;
            // Windows keeps the selected position, not the selected tab, while positions are relabelled: restore it after
            var selected = SelectedTab;
            while (at < last)
            {
                // relabel positions (TabPages[i] = page), as MoveTab does: no page is detached
                var next = TabPages[at + 1];
                TabPages[at + 1] = _plusPage;
                TabPages[at] = next;
                at++;
            }
            if (selected != null && selected != _plusPage && SelectedTab != selected) SelectedTab = selected;
        }
        finally
        {
            _adjusting = false;
            UpdateTabWidths();
            if (SelectedTab == _plusPage && TabCount > 0) SelectedIndex = TabCount - 1;
        }
    }

    // tabs share the strip like a browser's: up to 240 px each, all narrowing together as more open, down to 120 px;
    // only past that do Windows' scroll arrows appear (the + slot counts as one tab)
    private int MaxTabWidth => Px(240);
    private int MinTabWidth => Px(120);

    private bool _sizingTabs;

    private void UpdateTabWidths()
    {
        // Setting ItemSize makes the tab control nudge its own size by a pixel and back to re-lay itself out, which
        // fires OnResize and comes back here: ignore those, or each pass sets a new size and it never ends (3.21.0 at
        // first: endless recursion, a stack overflow at start-up)
        if (IsDisposed || _sizingTabs) return;
        _sizingTabs = true;
        try
        {
            int height = TextRenderer.MeasureText("Ag", Font).Height + Padding.Y * 2 + Px(4);
            int slots = Math.Max(1, base.TabCount);
            int width = Math.Clamp((ClientSize.Width - Px(4)) / slots, MinTabWidth, MaxTabWidth);
            var size = new Size(width, height);
            if (ItemSize != size) ItemSize = size;
        }
        finally { _sizingTabs = false; }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateTabWidths();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateTabWidths();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control != _plusPage) KeepPlusLast();
    }

    // ---- the scroll arrows Windows adds when the tabs don't fit (a native "msctls_updown32" child): drawn themed

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageInts(IntPtr hWnd, int msg, out int wParam, out int lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessagePtr(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_HSCROLL = 0x0114, SB_THUMBPOSITION = 4;
    private const int UDM_GETRANGE32 = 0x0400 + 112, UDM_SETPOS32 = 0x0400 + 113, UDM_GETPOS32 = 0x0400 + 114;

    /// <summary>
    /// The mouse wheel over the strip scrolls the tabs when they overflow (up/left, down/right; a tilt wheel or
    /// horizontal touchpad scroll too), one tab per notch. The selected tab doesn't change.
    /// </summary>
    private bool ScrollTabs(int steps)
    {
        if (!IsHandleCreated || steps == 0) return false;
        IntPtr h = FindWindowEx(Handle, IntPtr.Zero, "msctls_updown32", null);
        if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
        SendMessageInts(h, UDM_GETRANGE32, out int low, out int high);
        int pos = (int)SendMessagePtr(h, UDM_GETPOS32, IntPtr.Zero, IntPtr.Zero);
        int next = Math.Clamp(pos + steps, Math.Min(low, high), Math.Max(low, high));
        if (next == pos) return true;
        SendMessagePtr(h, UDM_SETPOS32, IntPtr.Zero, (IntPtr)next);
        // the same message the arrows send the tab control when clicked
        SendMessagePtr(Handle, WM_HSCROLL, (IntPtr)((next << 16) | SB_THUMBPOSITION), h);
        Invalidate();
        InvalidateRect(h, IntPtr.Zero, true);
        return true;
    }

    private ArrowsSkin _arrows;

    /// <summary>Where the scroll arrows are (client coordinates), or empty when the tabs fit. Skins them when they appear.</summary>
    private Rectangle ScrollArrowsRect()
    {
        if (!IsHandleCreated) return Rectangle.Empty;
        IntPtr h = FindWindowEx(Handle, IntPtr.Zero, "msctls_updown32", null);
        if (h == IntPtr.Zero || !IsWindowVisible(h)) return Rectangle.Empty;
        if (_arrows == null || _arrows.Handle != h)
        {
            _arrows = new ArrowsSkin(this);
            _arrows.AssignHandle(h);
            SetWindowTheme(h, "", "");              // classic style: no hover highlight drawn behind our back
            InvalidateRect(h, IntPtr.Zero, true);   // repaint it in the theme straight away
        }
        GetWindowRect(h, out var r);
        return RectangleToClient(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
    }

    /// <summary>Lets Windows handle the arrows (clicks, scrolling), then draws over them in the theme's colours.</summary>
    private sealed class ArrowsSkin : NativeWindow
    {
        private const int WM_PAINT = 0x000F, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                          WM_TIMER = 0x0113, WM_CAPTURECHANGED = 0x0215, WM_MOUSELEAVE = 0x02A3, WM_ENABLE = 0x000A;
        private readonly ThemedTabControl _owner;
        private int _hot = -1;   // the half under the cursor: 0 left, 1 right
        public ArrowsSkin(ThemedTabControl owner) { _owner = owner; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case WM_MOUSEMOVE:
                {
                    int x = (short)((long)m.LParam & 0xFFFF);
                    GetClientRect(Handle, out var r);
                    int hot = x < (r.Right - r.Left) / 2 ? 0 : 1;
                    if (hot != _hot)
                    {
                        _hot = hot;
                        var track = new TRACKMOUSEEVENT { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = Handle };
                        TrackMouseEvent(ref track);
                    }
                    Draw();
                    break;
                }
                case WM_MOUSELEAVE: _hot = -1; Draw(); break;
                // anything after which Windows may have drawn its own look: draw ours again
                case WM_PAINT: case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_TIMER: case WM_CAPTURECHANGED: case WM_ENABLE:
                    Draw();
                    break;
            }
        }

        private void Draw()
        {
            if (Handle == IntPtr.Zero) return;
            GetClientRect(Handle, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return;
            var p = Theme.Current;
            using var g = Graphics.FromHwnd(Handle);
            using (var back = new SolidBrush(p.Raised)) g.FillRectangle(back, 0, 0, w, h);
            if (_hot >= 0)
                using (var hover = new SolidBrush(p.Hover))
                    g.FillRectangle(hover, _hot == 0 ? 1 : w / 2 + 1, 1, w / 2 - 1, h - 2);
            using (var line = new Pen(p.Border))
            {
                g.DrawRectangle(line, 0, 0, w - 1, h - 1);
                g.DrawLine(line, w / 2, 3, w / 2, h - 4);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var arrow = new SolidBrush(p.Text);
            int a = Math.Max(3, Math.Min(w / 4, h / 3) / 2 + 1), cy = h / 2;
            int lx = w / 4, rx = w * 3 / 4;
            g.FillPolygon(arrow, new[] { new Point(lx - a / 2, cy), new Point(lx + a / 2 + 1, cy - a), new Point(lx + a / 2 + 1, cy + a) });
            g.FillPolygon(arrow, new[] { new Point(rx + a / 2, cy), new Point(rx - a / 2 - 1, cy - a), new Point(rx - a / 2 - 1, cy + a) });
        }
    }

    public ThemedTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Point(Px(22), Px(6)); // room for the × on the right
        SizeMode = TabSizeMode.Fixed;          // set before the handle exists (changing it later recreates the control)
        _plusPage.Tag = PlusTag;
        UpdateTabWidths();
    }

    private const string PlusTag = "switchboard:new-tab";

    /// <summary>The window moved to a screen with different scaling: the tab padding follows.</summary>
    protected override void RescaleConstantsForDpi(int deviceDpiOld, int deviceDpiNew)
    {
        base.RescaleConstantsForDpi(deviceDpiOld, deviceDpiNew);
        Padding = new Point((int)Math.Round(22 * deviceDpiNew / 96.0), (int)Math.Round(6 * deviceDpiNew / 96.0));
        UpdateTabWidths();
        Invalidate();
    }

    private Rectangle CloseRect(int i)
    {
        var r = GetTabRect(i);
        return new Rectangle(r.Right - CloseSize - Px(7), r.Top + (r.Height - CloseSize) / 2 + 1, CloseSize, CloseSize);
    }

    private int CloseHit(Point pt)
    {
        for (int i = 0; i < TabCount; i++)
            if (CloseRect(i).Contains(pt)) return i;
        return -1;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HSCROLL)
        {
            // the strip scrolled (arrows, wheel, or a tab being brought into view): Windows shifts the strip's pixels
            // and repaints only the revealed sliver, which would drag a copy of the fixed + along with the tabs.
            // Repaint the whole strip instead.
            base.WndProc(ref m);
            Invalidate();
            IntPtr arrows = FindWindowEx(Handle, IntPtr.Zero, "msctls_updown32", null);
            if (arrows != IntPtr.Zero) InvalidateRect(arrows, IntPtr.Zero, true);
            return;
        }
        if ((m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL) && TabCount > 0)
        {
            // over the strip (not the page below it): scroll the tabs when they overflow
            var pt = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            if (pt.Y >= 0 && pt.Y < GetTabRect(0).Bottom)
            {
                int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                int notches = Math.Max(1, Math.Abs(delta) / 120);
                // vertical wheel: up = left; horizontal (tilt / touchpad): right = right
                int steps = m.Msg == WM_MOUSEWHEEL ? (delta > 0 ? -notches : notches) : (delta > 0 ? notches : -notches);
                if (ScrollTabs(steps)) { m.Result = IntPtr.Zero; return; }
            }
        }
        // The Windows tab control calls the empty part of its tab strip "transparent", so clicks and hovers
        // there go to the window behind it. Claim it, so the + (and its hover highlight) work.
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTTRANSPARENT) m.Result = (IntPtr)HTCLIENT;
            return;
        }

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
            var plus = PlusRect();
            if (plus.Contains(pt))
            {
                var screen = PointToScreen(new Point(plus.Left, plus.Bottom));
                BeginInvoke(() => NewTabRequested?.Invoke(screen));
                return;
            }
            if (PlusSlot().Contains(pt)) return;   // the rest of the + slot: empty strip, not a tab to select
        }
        base.WndProc(ref m);
    }

    /// <summary>The real tab under a point (not the + slot), or -1.</summary>
    public int TabAt(Point pt)
    {
        for (int i = 0; i < TabCount; i++)
            if (GetTabRect(i).Contains(pt)) return i;
        return -1;
    }

    /// <summary>
    /// Move a tab to another position. Done by assigning pages to positions (TabPages[i] = page), which only relabels
    /// the strip: no page is removed from the control, so nothing inside it is detached (a remote desktop would drop
    /// its connection if its page were removed and inserted again).
    /// </summary>
    public void MoveTab(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= TabCount || to >= TabCount) return;
        var page = TabPages[from];
        int step = to > from ? 1 : -1;
        for (int i = from; i != to; i += step)
        {
            var neighbour = TabPages[i + step];
            TabPages[i + step] = page;
            TabPages[i] = neighbour;
        }
        SelectedIndex = to;   // shows the moved page at its new position
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragIndex = e.Button == MouseButtons.Left ? TabAt(e.Location) : -1;
        _dragStart = e.Location;
        _dragging = false;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Cursor = Cursors.Default;
            TabsReordered?.Invoke();
        }
        _dragIndex = -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex >= 0 && e.Button == MouseButtons.Left)
        {
            if (!_dragging && Math.Abs(e.X - _dragStart.X) >= SystemInformation.DragSize.Width)
            {
                _dragging = true;
                Cursor = Cursors.SizeWE;
            }
            if (_dragging)
            {
                // only the horizontal position matters, so the tab follows even if the pointer drifts off the strip
                int over = TabAt(new Point(e.X, GetTabRect(Math.Min(_dragIndex, TabCount - 1)).Top + 4));
                if (over >= 0 && over != _dragIndex)
                {
                    MoveTab(_dragIndex, over);
                    _dragIndex = over;
                }
                return;
            }
        }
        int h = CloseHit(e.Location);
        bool plus = PlusRect().Contains(e.Location);
        if (h != _hoverClose || plus != _hoverPlus) { _hoverClose = h; _hoverPlus = plus; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverClose != -1 || _hoverPlus) { _hoverClose = -1; _hoverPlus = false; Invalidate(); }
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        _hoverClose = -1;
        // after the removal has finished: drop the + if that was the last real tab, and re-share the width
        if (e.Control != _plusPage && IsHandleCreated) BeginInvoke(new Action(KeepPlusLast));
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        // the + slot is never the selected tab (closing the tab before it, or the keyboard, could land on it)
        if (SelectedTab == _plusPage && !_adjusting)
        {
            if (TabCount > 0) SelectedIndex = TabCount - 1;
            return;
        }
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
            var textRect = new Rectangle(r.Left + Px(12), r.Top, r.Width - Px(12) - CloseSize - Px(12), r.Height);
            string text = TabPages[i].Text;
            if (Equals(TabPages[i].Tag, ShellTag))
            {
                // terminal icon in the space IconSpace made, then the name
                var iconRect = new Rectangle(textRect.Left, textRect.Top, Px(18), textRect.Height);
                int iconSize = Px(16);
                if (TabIcons.TryGetValue(TabPages[i], out var icon))
                {
                    var oldMode = g.InterpolationMode;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, new Rectangle(iconRect.Left + (iconRect.Width - iconSize) / 2, iconRect.Top + (iconRect.Height - iconSize) / 2 + 1, iconSize, iconSize));
                    g.InterpolationMode = oldMode;
                }
                else
                    TextRenderer.DrawText(g, Icons.Terminal, Icons.Font, iconRect, sel ? p.AccentFore : p.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                text = text.TrimStart();
                textRect = new Rectangle(textRect.Left + Px(22), textRect.Top, Math.Max(0, textRect.Width - Px(22)), textRect.Height);
            }
            else if (TabPages[i].Tag is string tag && tag.StartsWith(BadgeTagPrefix, StringComparison.Ordinal))
            {
                // the badge in the room BadgeRoom made: AWS in AWS's orange, saved connections in the accent colour;
                // softened on tabs that aren't selected
                string badge = tag[BadgeTagPrefix.Length..];
                int w = BadgeWidth(badge);
                var pill = new Rectangle(textRect.Left, textRect.Top + (textRect.Height - BadgeHeight) / 2 + 1, w, BadgeHeight);
                DrawBadge(g, badge, BadgeFont, pill, sel, p);
                text = text.TrimStart();
                textRect = new Rectangle(textRect.Left + w + BadgeGap, textRect.Top, Math.Max(0, textRect.Width - w - BadgeGap), textRect.Height);
            }
            TextRenderer.DrawText(g, text, Font, textRect, sel ? p.Text : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            DrawClose(g, CloseRect(i), sel, i == _hoverClose, p);
        }

        // the + (its own slot at the end): drawn like the tabs' ×: muted strokes, an accent circle under the mouse
        var plusRect = PlusRect();
        if (!plusRect.IsEmpty)
        {
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (_hoverPlus)
            {
                using var hb = new SolidBrush(p.Accent);
                g.FillEllipse(hb, plusRect);
            }
            using var pen = new Pen(_hoverPlus ? p.AccentText : p.Muted, 1.6f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            int cx = plusRect.Left + plusRect.Width / 2, cy = plusRect.Top + plusRect.Height / 2, arm = Px(5);
            g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
            g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
            g.SmoothingMode = oldMode;
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

        // all the width right of the box: padding only adds room (taking it away here is what cut text short)
        var textRect = new Rectangle(box.Right + Gap, 0, Math.Max(0, ClientSize.Width - box.Right - Gap), ClientSize.Height);
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
