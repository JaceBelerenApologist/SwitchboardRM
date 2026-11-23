using System.Drawing;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>What a server's tooltip shows: a title, a subtitle, label/value rows, tags and a note.</summary>
internal sealed class TipContent
{
    public string Title = "", Subtitle = "", Note = "";
    public List<(string Label, string Value, Color? Color)> Rows = new();
    public List<(string Key, string Value)> Tags = new();
}

/// <summary>
/// The server list's tooltips. Windows' own list tooltips pop up under the cursor and take the mouse when it moves onto
/// them (moving down the list), so the list stops seeing which server is under the cursor. This one is a click-through,
/// mouse-transparent window: the list keeps getting every mouse move, even with the cursor over the tip. It draws a
/// formatted card (TipContent) when one is given for the item, otherwise the item's ToolTipText.
/// </summary>
internal sealed class HoverTip : IDisposable
{
    private readonly ListView _list;
    private readonly Func<ListViewItem, TipContent> _content;
    private readonly TipWindow _window = new();
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 450 };
    private ListViewItem _hovered;

    public HoverTip(ListView list, Func<ListViewItem, TipContent> content = null)
    {
        _list = list;
        _content = content;
        _list.ShowItemToolTips = false;   // Windows' own tooltips: replaced by this
        _list.MouseMove += (_, e) => Track(e.Location);
        _list.MouseLeave += (_, _) => { if (!_list.ClientRectangle.Contains(_list.PointToClient(Cursor.Position))) Hide(); };
        _list.MouseDown += (_, _) => Hide();
        _list.MouseWheel += (_, _) => Hide();
        _list.KeyDown += (_, _) => Hide();
        _list.Leave += (_, _) => Hide();
        _delay.Tick += (_, _) => { _delay.Stop(); Show(); };
    }

    private void Track(Point location)
    {
        var item = _list.GetItemAt(location.X, location.Y);
        if (item == _hovered) return;
        _hovered = item;
        _window.Hide();
        _delay.Stop();
        if (item != null) _delay.Start();
    }

    private void Show()
    {
        if (_hovered == null || _list.IsDisposed || !_list.Visible) return;
        TipContent content = null;
        try { content = _content?.Invoke(_hovered); } catch (Exception ex) { Log.Write("Tooltip: " + ex.Message); }
        if (content == null)
        {
            if (string.IsNullOrEmpty(_hovered.ToolTipText)) return;
            content = new TipContent { Note = _hovered.ToolTipText };
        }
        _window.Present(content, Cursor.Position);
    }

    public void Hide()
    {
        _delay.Stop();
        _hovered = null;
        _window.Hide();
    }

    public void Dispose()
    {
        _delay.Dispose();
        _window.Dispose();
    }

    /// <summary>The tip itself: borderless, never activated, never takes the mouse; themed, in the app font.</summary>
    private sealed class TipWindow : Form
    {
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        private const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
        private const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        private const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        // measuring must not ask for an ellipsis: with no width limit, Windows then reports the text as fitting in almost
        // nothing (the card came out a few characters wide). Drawing keeps the ellipsis for genuinely long values.
        private const TextFormatFlags Measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        private TipContent _c = new();
        private Font _title, _body, _small;
        private int _pad, _gap, _labelWidth, _valueWidth, _width;

        public TipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.97;   // a layered window: together with WS_EX_TRANSPARENT, clicks and the mouse pass straight through
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }   // the list underneath gets the mouse
            base.WndProc(ref m);
        }

        private int LineHeight(Font f) => TextRenderer.MeasureText("Ag", f, Size.Empty, Measure).Height;
        private int TextWidth(string s, Font f) => TextRenderer.MeasureText(s ?? "", f, Size.Empty, Measure).Width + 2;   // + a hair for the fake bold

        public void Present(TipContent content, Point cursor)
        {
            _c = content;
            // the app font (readable), not the server list's own font
            _title?.Dispose(); _body?.Dispose(); _small?.Dispose();
            _body = Theme.CreateFont(Theme.UiFont, 9F);
            _title = Theme.CreateFont(Theme.UiFont, 10.5F, FontStyle.Bold);
            _small = Theme.CreateFont(Theme.UiFont, 8F);
            _pad = LogicalToDeviceUnits(10);
            _gap = LogicalToDeviceUnits(14);
            int maxValue = LogicalToDeviceUnits(360), maxWidth = LogicalToDeviceUnits(520);

            var labels = _c.Rows.Select(r => r.Label).Concat(_c.Tags.Select(t => t.Key)).ToList();
            var values = _c.Rows.Select(r => r.Value).Concat(_c.Tags.Select(t => t.Value)).ToList();
            _labelWidth = labels.Count == 0 ? 0 : labels.Max(l => TextWidth(l, _body));
            _valueWidth = values.Count == 0 ? 0 : Math.Min(maxValue, values.Max(v => TextWidth(v, _body)));
            int widest = Math.Max(TextWidth(_c.Title, _title) + LogicalToDeviceUnits(2), TextWidth(_c.Subtitle, _small));
            if (labels.Count > 0) widest = Math.Max(widest, _labelWidth + _gap + _valueWidth);
            if (_c.Note.Length > 0)
                widest = Math.Max(widest, Math.Min(maxWidth, TextRenderer.MeasureText(_c.Note, _body, new Size(maxWidth, 0), Wrap).Width));
            _width = Math.Min(maxWidth, widest) + _pad * 2;
            var size = new Size(_width, Arrange(null) + _pad);

            // beside the cursor (below-right), flipped above / left near the screen's edges
            var screen = Screen.FromPoint(cursor).WorkingArea;
            int x = cursor.X + LogicalToDeviceUnits(14), y = cursor.Y + LogicalToDeviceUnits(20);
            if (x + size.Width > screen.Right) x = Math.Max(screen.Left, cursor.X - size.Width - LogicalToDeviceUnits(8));
            if (y + size.Height > screen.Bottom) y = Math.Max(screen.Top, cursor.Y - size.Height - LogicalToDeviceUnits(8));
            Bounds = new Rectangle(x, y, size.Width, size.Height);
            BackColor = Theme.Current.Raised;
            Invalidate();
            if (!Visible) Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var border = new Pen(Theme.Current.Border)) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            Arrange(e.Graphics);
        }

        /// <summary>Lays the card out top to bottom; draws it when g is given. Returns the height used.</summary>
        private int Arrange(Graphics g)
        {
            var p = Theme.Current;
            int x = _pad, y = _pad, inner = _width - _pad * 2;
            void Draw(string s, Font f, Color c, int left, int top, int width)
            {
                if (g == null) return;
                TextRenderer.DrawText(g, s, f, new Rectangle(left, top, width, LineHeight(f)), c, Line);
                if (Theme.IsFauxBold(f))   // a font without a bold: thicken it by hand, as the menus do
                    TextRenderer.DrawText(g, s, f, new Rectangle(left + 1, top, width, LineHeight(f)), c, Line);
            }

            if (_c.Title.Length > 0) { Draw(_c.Title, _title, p.Text, x, y, inner); y += LineHeight(_title); }
            if (_c.Subtitle.Length > 0) { y += LogicalToDeviceUnits(1); Draw(_c.Subtitle, _small, p.Muted, x, y, inner); y += LineHeight(_small); }
            if ((_c.Title.Length > 0 || _c.Subtitle.Length > 0) && (_c.Rows.Count > 0 || _c.Tags.Count > 0 || _c.Note.Length > 0))
            {
                y += LogicalToDeviceUnits(6);
                if (g != null) using (var line = new Pen(p.Border)) g.DrawLine(line, x, y, x + inner, y);
                y += LogicalToDeviceUnits(6);
            }

            int row = LineHeight(_body) + LogicalToDeviceUnits(2);
            int valueLeft = x + _labelWidth + _gap, valueWidth = inner - _labelWidth - _gap;
            foreach (var (label, value, color) in _c.Rows)
            {
                Draw(label, _body, p.Muted, x, y, _labelWidth);
                Draw(value, _body, color ?? p.Text, valueLeft, y, valueWidth);
                y += row;
            }
            if (_c.Tags.Count > 0)
            {
                y += LogicalToDeviceUnits(_c.Rows.Count > 0 ? 6 : 0);
                Draw("TAGS", _small, p.Muted, x, y, inner);
                y += LineHeight(_small) + LogicalToDeviceUnits(2);
                foreach (var (key, value) in _c.Tags)
                {
                    Draw(key, _body, p.Muted, x, y, _labelWidth);
                    Draw(value, _body, p.Text, valueLeft, y, valueWidth);
                    y += row;
                }
            }
            if (_c.Note.Length > 0)
            {
                if (_c.Rows.Count > 0 || _c.Tags.Count > 0) y += LogicalToDeviceUnits(6);
                int h = TextRenderer.MeasureText(_c.Note, _body, new Size(inner, 0), Wrap).Height;
                if (g != null) TextRenderer.DrawText(g, _c.Note, _body, new Rectangle(x, y, inner, h), p.Text, Wrap);
                y += h;
            }
            return y;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _title?.Dispose(); _body?.Dispose(); _small?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
