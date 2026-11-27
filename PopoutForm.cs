namespace SwitchboardRM;

/// <summary>
/// A session in a window of its own (tab menu > Pop out to own window). MainForm moves the session's controls (the
/// remote desktop, terminal, VNC view or web page) out of its tab page into <see cref="Body"/>, and back again on
/// Return. The tab page itself stays out of the strips but isn't disposed: it keeps the session's title (MainForm
/// updates the page's text as the session changes) and is where the controls go back to. Controls are moved, never the
/// page: taking a page out of its strip with a remote desktop on it drops the connection (CLAUDE.md lesson 26a).
/// </summary>
internal sealed class PopoutForm : Form
{
    public Session Session { get; }

    /// <summary>Where the session's controls live while popped out.</summary>
    public Panel Body { get; } = new() { Dock = DockStyle.Fill };

    /// <summary>Return to main window was clicked.</summary>
    public event Action<PopoutForm> ReturnRequested;
    public event Action<PopoutForm> ReconnectRequested;
    public event Action<PopoutForm> CtrlAltDelRequested;

    /// <summary>
    /// The user wants the session ended, like closing its tab: × or Alt+F4 (ask = false: straight away, as a tab's ×) or
    /// Ctrl+Shift+W (ask = true, as in the main window). Not raised when MainForm closes the window itself (returning
    /// the session, or the session ending).
    /// </summary>
    public event Action<PopoutForm, bool> CloseRequested;

    /// <summary>Set by MainForm before it closes the window itself, so closing doesn't also end the session.</summary>
    public bool ClosingQuietly;

    private readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(6, 0, 4, 0) };
    private readonly BadgeLabel _title = new();
    private readonly ToolStripButton _btnReturn = new(Icons.Undock) { ToolTipText = "Return to the main window" };
    private readonly ToolStripButton _btnFull = new(Icons.FullScreen) { ToolTipText = "Full screen" };
    private readonly ToolStripButton _btnTop = new(Icons.Pin) { ToolTipText = "Keep this window on top of other windows" };
    private readonly ToolStripButton _btnCad = new(Icons.Keyboard) { ToolTipText = "Send Ctrl+Alt+Del (Ctrl+Alt+End does the same from the keyboard)" };
    private readonly ToolStripButton _btnReconnect = new(Icons.Refresh) { ToolTipText = "Reconnect" };

    // full screen: the window's frame goes and it covers the screen it's on; this is what it goes back to
    private Rectangle _windowedBounds;
    private FormWindowState _windowedState;
    public bool FullScreen { get; private set; }

    public PopoutForm(Session session)
    {
        Session = session;
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        ShowInTaskbar = true;   // a window of its own: its own taskbar button, Alt+Tab, any screen
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        // only what applies to this kind of session: Ctrl+Alt+Del and full screen for a remote screen, Reconnect for
        // anything remote
        bool remoteScreen = session.Rdp != null || session.Vnc != null;
        _btnFull.Visible = remoteScreen;
        _btnCad.Visible = remoteScreen;
        _btnReconnect.Visible = session.Kind is Protocol.Rdp or Protocol.Ssh or Protocol.Vnc;

        _bar.Items.Add(_title);
        // right-aligned: the first added sits furthest right
        foreach (var b in new[] { _btnReturn, _btnFull, _btnTop, _btnCad, _btnReconnect })
        {
            b.Alignment = ToolStripItemAlignment.Right;
            b.Font = Icons.Font;
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.Padding = new Padding(4, 0, 4, 0);
            _bar.Items.Add(b);
        }
        _btnReturn.Click += (_, _) => ReturnRequested?.Invoke(this);
        _btnFull.Click += (_, _) => SetFullScreen(!FullScreen);
        _btnTop.Click += (_, _) => SetOnTop(!TopMost);
        _btnCad.Click += (_, _) => CtrlAltDelRequested?.Invoke(this);
        _btnReconnect.Click += (_, _) => ReconnectRequested?.Invoke(this);

        Controls.Add(Body);
        Controls.Add(_bar);   // added after the body, so it docks above it
        ApplyTheme();
        UpdateTitle();
        session.Page.TextChanged += OnPageTextChanged;
    }

    /// <summary>The app's colours and font, as now (MainForm calls this again when they change).</summary>
    public void ApplyTheme()
    {
        Theme.Apply(this, Theme.Current.IsDark);
        Body.BackColor = Theme.Current.Back;
        _btnTop.Checked = TopMost;
        _bar.Invalidate();
    }

    private void OnPageTextChanged(object sender, EventArgs e) => UpdateTitle();

    /// <summary>
    /// The window and toolbar show the session's title as its tab did: the tab's badge (AWS / RDP / SSH / VNC) drawn the
    /// same way, then the name (without the spaces that leave room for the badge in the tab).
    /// </summary>
    private void UpdateTitle()
    {
        string name = Session.Page.Text.Trim();
        Text = name + " · Switchboard";
        _title.Text = name;
        _title.SetBadge(Session.Page.Tag is string tag && tag.StartsWith(ThemedTabControl.BadgeTagPrefix, StringComparison.Ordinal)
            ? tag[ThemedTabControl.BadgeTagPrefix.Length..] : "");
        _bar.PerformLayout();
        _bar.Invalidate();
    }

    public void SetOnTop(bool on)
    {
        TopMost = on;
        _btnTop.Checked = on;
        _btnTop.Text = on ? Icons.Unpin : Icons.Pin;
        _btnTop.ToolTipText = on ? "Stop keeping this window on top" : "Keep this window on top of other windows";
    }

    /// <summary>
    /// Full screen: no frame, covering the whole screen the window is on (taskbar too), with the toolbar kept so there's
    /// always a way back (a remote desktop takes Esc and other keys for itself, so a key couldn't be relied on).
    /// </summary>
    public void SetFullScreen(bool on)
    {
        if (on == FullScreen) return;
        if (on)
        {
            _windowedState = WindowState;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            _windowedBounds = Bounds;
            var screen = Screen.FromControl(this);
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _windowedBounds;
            WindowState = _windowedState;
        }
        FullScreen = on;
        _btnFull.Text = on ? Icons.ExitFullScreen : Icons.FullScreen;
        _btnFull.ToolTipText = on ? "Leave full screen" : "Full screen";
        Session.Rdp?.SyncSize();
    }

    /// <summary>Where the window sits when it isn't maximized or full screen (what to save for next time).</summary>
    public Rectangle WindowedBounds =>
        FullScreen ? _windowedBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

    /// <summary>Maximized (or was, before going full screen).</summary>
    public bool WindowedMaximized => FullScreen ? _windowedState == FormWindowState.Maximized : WindowState == FormWindowState.Maximized;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Shift+W closes it, as it closes a tab (MainForm asks first); inside a terminal the page sends it itself
        if (keyData == (Keys.Control | Keys.Shift | Keys.W)) { CloseRequested?.Invoke(this, true); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // × / Alt+F4 end the session (no question, like a tab's ×); MainForm then closes this window quietly
        if (!ClosingQuietly && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke(this, false);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Session.Page.TextChanged -= OnPageTextChanged;
        base.OnFormClosed(e);
    }

    /// <summary>The toolbar's title: the tab's badge pill (if it has one), then the name.</summary>
    private sealed class BadgeLabel : ToolStripLabel
    {
        private string _badge = "";
        private Font _badgeFont;

        public void SetBadge(string badge) { _badge = badge ?? ""; Owner?.PerformLayout(); Invalidate(); }

        private int Px(int pixelsAt100) => (int)Math.Round(pixelsAt100 * (Owner?.DeviceDpi ?? 96) / 96.0);

        // the badge font follows the label's, as in the tab strips (7/9 of the size, bold)
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

        private int PillWidth => TextRenderer.MeasureText(_badge, BadgeFont, Size.Empty, TextFormatFlags.NoPadding).Width + Px(10);

        private int Room => _badge.Length == 0 ? 0 : PillWidth + Px(6);

        // drawn here in full (pill, then name): the themed renderer places item text itself and ignored room left by
        // padding, so the name ended up under the pill
        public override Size GetPreferredSize(Size constrainingSize)
        {
            var text = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return new Size(Room + text.Width + Px(4), Math.Max(text.Height, Px(16)) + Px(4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int x = 0;
            if (_badge.Length > 0)
            {
                int h = Px(16);
                ThemedTabControl.DrawBadge(e.Graphics, _badge, BadgeFont, new Rectangle(0, (Height - h) / 2, PillWidth, h), true, Theme.Current);
                x = Room;
            }
            TextRenderer.DrawText(e.Graphics, Text ?? "", Font, new Rectangle(x, 0, Math.Max(0, Width - x), Height), Theme.Current.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _badgeFont?.Dispose();
            base.Dispose(disposing);
        }
    }
}
