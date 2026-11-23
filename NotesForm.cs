using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// Release notes (the update notice's What's new, and Settings > About > What's new): a header with the app icon,
/// then the changelog's markdown laid out properly by NotesView, and Open the release page / Update now / Close.
/// </summary>
internal sealed class NotesForm : Form
{
    private readonly string _title, _subtitle, _pageUrl;

    /// <summary>Update now was clicked (only offered when the caller can update).</summary>
    public bool UpdateRequested { get; private set; }

    public NotesForm(string title, string subtitle, string markdown, string pageUrl, bool offerUpdate = false)
    {
        _title = title;
        _subtitle = subtitle ?? "";
        _pageUrl = pageUrl ?? "";
        Text = title;
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        KeyPreview = true;
        ClientSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(620));
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(360));

        var header = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(78) };
        header.Paint += PaintHeader;
        var view = new NotesView(markdown) { Dock = DockStyle.Fill };

        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(96, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = LogicalToDeviceUnits(52), Padding = new Padding(16, 8, 16, 8) };
        buttons.Controls.Add(close);
        if (offerUpdate)
        {
            var update = new Button { Text = "Update now", Tag = "primary", AutoSize = true, MinimumSize = new Size(120, 32), Margin = new Padding(8, 0, 0, 0) };
            update.Click += (_, _) => { UpdateRequested = true; DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(update);
        }
        if (_pageUrl.Length > 0)
        {
            var page = new Button { Text = "Open the release page", AutoSize = true, MinimumSize = new Size(160, 32), Margin = new Padding(8, 0, 0, 0) };
            page.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_pageUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("Couldn't open " + _pageUrl + ": " + ex.Message); }
            };
            buttons.Controls.Add(page);
        }
        CancelButton = close;
        Controls.Add(view);
        Controls.Add(header);
        Controls.Add(buttons);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => view.Focus();   // so the mouse wheel and arrow keys scroll straight away
        Theme.Apply(this, Theme.Current.IsDark);
        header.BackColor = Theme.Current.Raised;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private void PaintHeader(object sender, PaintEventArgs e)
    {
        var panel = (Panel)sender;
        var p = Theme.Current;
        var g = e.Graphics;
        int pad = LogicalToDeviceUnits(20), icon = LogicalToDeviceUnits(40);
        int top = (panel.Height - icon) / 2;
        if (AppIcon.Icon != null)
        {
            using var big = new Icon(AppIcon.Icon, icon, icon);
            g.DrawIcon(big, new Rectangle(pad, top, icon, icon));
        }
        using var titleFont = Theme.CreateFont(Theme.UiFont, 14F, FontStyle.Bold);
        using var subFont = Theme.CreateFont(Theme.UiFont, 9F);
        int x = pad + icon + LogicalToDeviceUnits(14);
        int titleH = TextRenderer.MeasureText("Ag", titleFont).Height, subH = _subtitle.Length > 0 ? TextRenderer.MeasureText("Ag", subFont).Height : 0;
        int y = (panel.Height - titleH - subH) / 2;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        var titleRect = new Rectangle(x, y, panel.Width - x - pad, titleH);
        TextRenderer.DrawText(g, _title, titleFont, titleRect, p.Text, flags);
        if (Theme.IsFauxBold(titleFont)) { titleRect.Offset(1, 0); TextRenderer.DrawText(g, _title, titleFont, titleRect, p.Text, flags); }
        if (subH > 0) TextRenderer.DrawText(g, _subtitle, subFont, new Rectangle(x, y + titleH, panel.Width - x - pad, subH), p.Muted, flags);
        using var line = new Pen(p.Border);
        g.DrawLine(line, 0, panel.Height - 1, panel.Width, panel.Height - 1);
    }
}

/// <summary>
/// The changelog's markdown, laid out and drawn: version headings with their date and an accent rule, bullets with
/// hanging indents (and sub-points), numbered steps, "Fixed:" as a FIXED tag, and **bold** and `code` inline.
/// </summary>
internal sealed class NotesView : ScrollableControl
{
    private enum Kind { Version, Paragraph, Bullet, Number }
    private sealed record Block(Kind Kind, int Level, string Text, string Extra = "");
    private enum Style { Normal, Bold, Code }

    private readonly List<Block> _blocks;
    private Font _body, _bold, _code, _version, _small;

    public NotesView(string markdown)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        TabStop = true;
        _blocks = Parse(markdown ?? "");
    }

    // ---------------------------------------------------------------- parsing (the CHANGELOG's markdown)

    private static List<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        string pendingText = null;
        Kind pendingKind = Kind.Paragraph;
        int pendingLevel = 0;
        string pendingExtra = "";

        void Flush()
        {
            if (pendingText != null) blocks.Add(new Block(pendingKind, pendingLevel, pendingText.Trim(), pendingExtra));
            pendingText = null;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (line.StartsWith("# ")) { Flush(); continue; }   // the file's own title
            var heading = Regex.Match(line, @"^##\s+(\S+)\s*(?:\((.*)\))?");
            if (heading.Success) { Flush(); blocks.Add(new Block(Kind.Version, 0, heading.Groups[1].Value, heading.Groups[2].Value)); continue; }

            int indent = line.Length - line.TrimStart().Length;
            string text = line.TrimStart();
            var bullet = Regex.Match(text, @"^[-*]\s+(.*)$");
            var number = Regex.Match(text, @"^(\d+)\.\s+(.*)$");
            if (bullet.Success) { Flush(); pendingKind = Kind.Bullet; pendingLevel = indent / 2; pendingText = bullet.Groups[1].Value; pendingExtra = ""; }
            else if (number.Success) { Flush(); pendingKind = Kind.Number; pendingLevel = indent / 2; pendingText = number.Groups[2].Value; pendingExtra = number.Groups[1].Value; }
            else if (pendingText != null) pendingText += " " + text;   // a wrapped line of the item above
            else { pendingKind = Kind.Paragraph; pendingLevel = 0; pendingText = text; pendingExtra = ""; }
        }
        Flush();
        return blocks;
    }

    /// <summary>**bold** and `code` runs; [text](link) shows its text.</summary>
    private static List<(string Text, Style Style)> Runs(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        var runs = new List<(string, Style)>();
        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|`([^`]+)`|[^*`]+|[*`]"))
        {
            if (m.Groups[1].Success) runs.Add((m.Groups[1].Value, Style.Bold));
            else if (m.Groups[2].Success) runs.Add((m.Groups[2].Value, Style.Code));
            else runs.Add((m.Value, Style.Normal));
        }
        return runs;
    }

    // ---------------------------------------------------------------- layout and drawing

    private void EnsureFonts()
    {
        if (_body != null) return;
        _body = Theme.CreateFont(Theme.UiFont, 10F);
        _bold = Theme.CreateFont(Theme.UiFont, 10F, FontStyle.Bold);
        _version = Theme.CreateFont(Theme.UiFont, 15F, FontStyle.Bold);
        _small = Theme.CreateFont(Theme.UiFont, 8F, FontStyle.Bold);
        _code = Theme.CreateFont(Theme.TerminalFont, 9F);
    }

    // PreserveGraphicsTranslateTransform: TextRenderer otherwise ignores the scroll offset (TranslateTransform in OnPaint)
    // and draws text where it would be unscrolled, while shapes do move
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                                          TextFormatFlags.PreserveGraphicsTranslateTransform;
    private const TextFormatFlags MeasureFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
    private static int W(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, MeasureFlags).Width;
    private static int H(Font f) => TextRenderer.MeasureText("Ag", f, Size.Empty, MeasureFlags).Height;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateScroll();
    }

    private void UpdateScroll()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0) return;
        int height = Arrange(null);
        AutoScrollMinSize = new Size(0, height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Back);
        e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        Arrange(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    // scrolling only repaints the newly shown strip by default; repaint everything so nothing is left behind
    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int line = H(_body ?? Font) * 2, page = ClientSize.Height - line;
        int y = -AutoScrollPosition.Y;
        y = e.KeyCode switch
        {
            Keys.Up => y - line, Keys.Down => y + line, Keys.PageUp => y - page, Keys.PageDown => y + page,
            Keys.Home => 0, Keys.End => AutoScrollMinSize.Height, _ => y,
        };
        AutoScrollPosition = new Point(0, Math.Max(0, y));
        Invalidate();
    }

    /// <summary>Lays everything out top to bottom at the current width; draws it when g is given. Returns the height.</summary>
    private int Arrange(Graphics g)
    {
        EnsureFonts();
        var p = Theme.Current;
        int pad = LogicalToDeviceUnits(24);
        int right = Math.Max(pad + LogicalToDeviceUnits(120), ClientSize.Width - pad - (VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth));
        int y = LogicalToDeviceUnits(14);
        bool firstVersion = true;

        foreach (var b in _blocks)
        {
            switch (b.Kind)
            {
                case Kind.Version:
                {
                    if (!firstVersion) y += LogicalToDeviceUnits(18);
                    firstVersion = false;
                    DrawText(g, b.Text, _version, p.Text, pad, y);
                    if (b.Extra.Length > 0)
                    {
                        int dx = W(b.Text, _version) + LogicalToDeviceUnits(12);
                        int dy = H(_version) - H(_body) - LogicalToDeviceUnits(2);
                        DrawText(g, b.Extra, _body, p.Muted, pad + dx, y + dy);
                    }
                    y += H(_version) + LogicalToDeviceUnits(6);
                    if (g != null)
                    {
                        using var accent = new SolidBrush(p.Accent);
                        g.FillRectangle(accent, pad, y, LogicalToDeviceUnits(36), LogicalToDeviceUnits(3));
                        using var rule = new Pen(p.Border);
                        g.DrawLine(rule, pad + LogicalToDeviceUnits(40), y + 1, right, y + 1);
                    }
                    y += LogicalToDeviceUnits(14);
                    break;
                }
                case Kind.Paragraph:
                    y = Flow(g, Runs(b.Text), pad, right, y, p.Text) + LogicalToDeviceUnits(8);
                    break;
                default:
                {
                    int indent = pad + b.Level * LogicalToDeviceUnits(22);
                    int textLeft = indent + LogicalToDeviceUnits(18);
                    int line = H(_body);
                    if (b.Kind == Kind.Number)
                        DrawText(g, b.Extra + ".", _bold, p.AccentFore, indent, y);
                    else if (g != null)
                    {
                        int d = LogicalToDeviceUnits(b.Level == 0 ? 6 : 5);
                        var old = g.SmoothingMode;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using var dot = new SolidBrush(b.Level == 0 ? p.Accent : p.Muted);
                        g.FillEllipse(dot, indent + LogicalToDeviceUnits(2), y + (line - d) / 2, d, d);
                        g.SmoothingMode = old;
                    }
                    string text = b.Text;
                    int start = textLeft;
                    if (text.StartsWith("Fixed:", StringComparison.Ordinal))
                    {
                        // "Fixed:" as a small tag
                        text = text["Fixed:".Length..].TrimStart();
                        start = DrawTag(g, "FIXED", p.Good, textLeft, y, line) + LogicalToDeviceUnits(8);
                    }
                    y = Flow(g, Runs(text), textLeft, right, y, p.Text, start) + LogicalToDeviceUnits(b.Level == 0 ? 7 : 4);
                    break;
                }
            }
        }
        return y + LogicalToDeviceUnits(16);
    }

    /// <summary>Wrapped text in runs (normal, bold, code) from left to right; the first line can start further in.</summary>
    private int Flow(Graphics g, List<(string Text, Style Style)> runs, int left, int right, int y, Color color, int firstX = -1)
    {
        int x = firstX >= 0 ? firstX : left;
        int line = H(_body) + LogicalToDeviceUnits(4);
        foreach (var (text, style) in runs)
        {
            var font = style switch { Style.Bold => _bold, Style.Code => _code, _ => _body };
            // words and the spaces after them, so lines break between words
            foreach (Match word in Regex.Matches(text, @"\S+\s*|\s+"))
            {
                string w = word.Value;
                int width = W(w.TrimEnd(), font), full = W(w, font);
                if (style == Style.Code) { width += LogicalToDeviceUnits(8); full += LogicalToDeviceUnits(8); }
                if (x + width > right && x > left) { x = left; y += line; }
                if (x == left && w.Trim().Length == 0) continue;   // no leading spaces on a new line
                if (g != null)
                {
                    int top = y + (H(_body) - H(font)) / 2;
                    if (style == Style.Code)
                    {
                        using var bg = new SolidBrush(Theme.Current.Raised);
                        g.FillRectangle(bg, x, y, width, H(_body));
                        DrawText(g, w.TrimEnd(), font, Theme.Current.Text, x + LogicalToDeviceUnits(4), top);
                    }
                    else DrawText(g, w, font, color, x, top);
                }
                x += style == Style.Code ? width + (full - width > LogicalToDeviceUnits(8) ? W(" ", _body) : 0) : full;
            }
        }
        return y + line;
    }

    /// <summary>A small rounded tag (e.g. FIXED); returns where the text after it starts.</summary>
    private int DrawTag(Graphics g, string label, Color fill, int x, int y, int lineHeight)
    {
        int w = W(label, _small) + LogicalToDeviceUnits(10), h = Math.Min(lineHeight, H(_small) + LogicalToDeviceUnits(4));
        var r = new Rectangle(x, y + (lineHeight - h) / 2, w, h);
        if (g != null)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            int d = LogicalToDeviceUnits(6);
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, label, _small, r, ColorMath.TextOn(fill),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                TextFormatFlags.PreserveGraphicsTranslateTransform);
        }
        return x + w;
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, int x, int y)
    {
        if (g == null) return;
        TextRenderer.DrawText(g, text, font, new Point(x, y), color, Flags);
        if (Theme.IsFauxBold(font)) TextRenderer.DrawText(g, text, font, new Point(x + 1, y), color, Flags);   // a font without a bold
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _body?.Dispose(); _bold?.Dispose(); _code?.Dispose(); _version?.Dispose(); _small?.Dispose(); }
        base.Dispose(disposing);
    }
}
