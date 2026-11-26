using System.Drawing;
using System.Runtime.InteropServices;
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
    private readonly Control _list;                 // the control it serves (the server list, the tab strip ...)
    private readonly Func<Point, object> _hitTest; // what's under the cursor there (an item, a tab), or null
    private readonly Func<object, TipContent> _content;
    private readonly TipWindow _window = new();
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 450 };
    // while the tip shows: hide it if the cursor has left the list or Switchboard isn't the active app any more
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    private object _hovered;
    private Form _form;
    private Point _lastMove = new(-1, -1);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>Switchboard's main window (or the tip itself) is the one in front.</summary>
    private bool AppInFront()
    {
        var fg = GetForegroundWindow();
        return _form != null && _form.IsHandleCreated && (fg == _form.Handle || fg == _window.WindowHandle);
    }

    /// <summary>For a list: an item's card (or its ToolTipText). Windows' own list tooltips are switched off.</summary>
    public HoverTip(ListView list, Func<ListViewItem, TipContent> content = null)
        : this(list, pt => list.GetItemAt(pt.X, pt.Y), o => o is ListViewItem item ? content?.Invoke(item) : null)
    {
        list.ShowItemToolTips = false;   // Windows' own tooltips: replaced by this
    }

    /// <summary>For any control: hitTest says what's under the cursor (null for nothing), content gives its card.</summary>
    public HoverTip(Control control, Func<Point, object> hitTest, Func<object, TipContent> content)
    {
        _list = control;
        _hitTest = hitTest;
        _content = content;
        _list.MouseMove += (_, e) => Track(e.Location);
        _list.MouseLeave += (_, _) => { if (!_list.ClientRectangle.Contains(_list.PointToClient(Cursor.Position))) Hide(); };
        _list.MouseDown += (_, _) => Hide();
        _list.MouseWheel += (_, _) => Hide();
        _list.KeyDown += (_, _) => Hide();
        _list.Leave += (_, _) => Hide();
        _delay.Tick += (_, _) => { _delay.Stop(); Show(); };
        _watch.Tick += (_, _) =>
        {
            bool overList = _list.IsHandleCreated && _list.Visible &&
                            _list.RectangleToScreen(_list.ClientRectangle).Contains(Cursor.Position);
            if (!overList || !AppInFront()) Hide();
        };
        // a dialog opening, another app coming forward, minimizing or going to the tray: the main window stops being
        // the active one, and the tip goes (it's always-on-top, so it would otherwise stay over everything)
        _list.HandleCreated += (_, _) => WatchForm();
        _list.ParentChanged += (_, _) => WatchForm();
        _list.VisibleChanged += (_, _) => { if (!_list.Visible) Hide(); };
        WatchForm();
    }

    private void WatchForm()
    {
        var form = _list.FindForm();
        if (form == null || form == _form) return;
        if (_form != null) { _form.Deactivate -= OnFormDeactivate; _form.Resize -= OnFormDeactivate; }
        _form = form;
        _form.Deactivate += OnFormDeactivate;
        _form.Resize += OnFormDeactivate;   // minimized or hidden to the tray
    }

    private void OnFormDeactivate(object sender, EventArgs e)
    {
        // still in front (a resize, or the tip itself): nothing to do
        if (_form is { WindowState: not FormWindowState.Minimized, Visible: true } && AppInFront()) return;
        Hide();
    }

    private void Track(Point location)
    {
        // Windows sends a mouse-move when a window appears or disappears under the cursor, even without movement:
        // ignore those, or a tip that hides would bring itself straight back
        if (location == _lastMove) return;
        _lastMove = location;
        object item = null;
        try { item = _hitTest(location); } catch { }
        if (Equals(item, _hovered)) return;
        _hovered = item;
        _window.HideTip();
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
            if (_hovered is not ListViewItem { ToolTipText.Length: > 0 } item) return;
            content = new TipContent { Note = item.ToolTipText };
        }
        if (!AppInFront()) return;   // not while a dialog or another app is in front
        _window.Present(content, Cursor.Position);
        _watch.Start();
    }

    public void Hide()
    {
        _delay.Stop();
        _watch.Stop();
        _hovered = null;
        _window.HideTip();
    }

    public void Dispose()
    {
        _delay.Dispose();
        _watch.Dispose();
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
            // not TopMost = true: WinForms applies that in a way that can activate the window (which hid it again, in a
            // loop). Always-on-top comes from WS_EX_TOPMOST and SetWindowPos with SWP_NOACTIVATE instead.
            Opacity = 0.97;   // a layered window: together with WS_EX_TRANSPARENT, clicks and the mouse pass straight through
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private bool _shown;

        /// <summary>The tip's window handle, or zero before it's created (for the foreground check).</summary>
        public IntPtr WindowHandle => IsHandleCreated ? Handle : IntPtr.Zero;

        /// <summary>Show at these bounds, on top, without activating (never takes the focus from the main window).</summary>
        private void ShowTip(Rectangle bounds)
        {
            SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (!_shown) ShowWindow(Handle, SW_SHOWNOACTIVATE);
            _shown = true;
            Invalidate();
        }

        public void HideTip()
        {
            if (!_shown || !IsHandleCreated) return;
            ShowWindow(Handle, SW_HIDE);
            _shown = false;
        }

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
            BackColor = Theme.Current.Raised;
            ShowTip(new Rectangle(x, y, size.Width, size.Height));
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
