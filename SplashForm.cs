using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>Shown while the app signs in and loads the server list. Paints itself in the current theme.</summary>
internal sealed class SplashForm : Form
{
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
    private readonly Font _titleFont = new("Segoe UI Semibold", 17F);
    private readonly Font _subFont = new("Segoe UI", 9.5F);
    private readonly Font _iconFont = new(Icons.Font.FontFamily, 30F);
    private string _status = "Starting ...";
    private DateTime _stepStart = DateTime.Now;
    private float _pos = -0.35f;   // position of the moving segment, as a fraction of the bar
    private int _lastSecs = -1;

    private const string MonitorGlyph = "\uE7F4"; // a screen, from the Windows icon font

    public SplashForm(bool dark)
    {
        Theme.Use(dark);
        Text = AppInfo.Name;
        if (AppIcon.Icon != null) Icon = AppIcon.Icon; // shown on the taskbar while starting
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 220);
        ShowInTaskbar = true; // so it's clear the app is starting
        DoubleBuffered = true;
        BackColor = Theme.Current.Back;

        _anim.Tick += (_, _) =>
        {
            _pos += 0.011f;
            if (_pos > 1.0f) _pos = -0.35f;
            Invalidate(BarRect);

            int secs = (int)(DateTime.Now - _stepStart).TotalSeconds;
            if (secs != _lastSecs) { _lastSecs = secs; Invalidate(StatusRect); }
        };
        _anim.Start();
    }

    /// <summary>Show what's happening right now, e.g. "Checking AWS sign-in ...".</summary>
    public void SetStatus(string text)
    {
        _status = text;
        _stepStart = DateTime.Now;
        _lastSecs = -1;
        Invalidate(StatusRect);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.RoundCorners(this);
    }

    private Rectangle BarRect => new(28, ClientSize.Height - 34, ClientSize.Width - 56, 4);
    private Rectangle StatusRect => new(28, ClientSize.Height - 62, ClientSize.Width - 56, 22);

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Back);

        // accent stripe and border
        using (var accent = new SolidBrush(p.Accent)) g.FillRectangle(accent, 0, 0, ClientSize.Width, 4);
        using (var border = new Pen(p.Border)) g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        // logo (or the monitor glyph if it's missing), name, tagline
        if (AppIcon.Logo is { } logo)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(logo, new Rectangle(24, 30, 50, 50));
        }
        else
            TextRenderer.DrawText(g, MonitorGlyph, _iconFont, new Point(24, 34), p.Accent, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, AppInfo.Name, _titleFont, new Point(84, 36), p.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Remote Desktop and SSH, through AWS or direct", _subFont, new Point(86, 72), p.Muted,
            TextFormatFlags.NoPadding);

        // version, top right
        string ver = "Version " + AppInfo.Version;
        var verSize = TextRenderer.MeasureText(ver, _subFont);
        TextRenderer.DrawText(g, ver, _subFont, new Point(ClientSize.Width - verSize.Width - 18, 18), p.Muted,
            TextFormatFlags.NoPadding);

        // status, with a seconds counter once a step takes a while
        int secs = (int)(DateTime.Now - _stepStart).TotalSeconds;
        string status = secs >= 3 ? $"{_status}  ({secs}s)" : _status;
        TextRenderer.DrawText(g, status, _subFont, StatusRect, p.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        // indeterminate progress: a purple segment sliding along a track
        var bar = BarRect;
        using (var track = new SolidBrush(p.Raised)) g.FillRectangle(track, bar);
        int segW = (int)(bar.Width * 0.35f);
        int x = bar.Left + (int)(bar.Width * _pos);
        var seg = Rectangle.Intersect(bar, new Rectangle(x, bar.Top, segW, bar.Height));
        if (seg.Width > 0)
        {
            using var fill = new LinearGradientBrush(new Rectangle(x, bar.Top, segW, bar.Height),
                Color.FromArgb(0, p.Accent), p.Accent, LinearGradientMode.Horizontal);
            g.FillRectangle(fill, seg);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _titleFont.Dispose();
            _subFont.Dispose();
            _iconFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
