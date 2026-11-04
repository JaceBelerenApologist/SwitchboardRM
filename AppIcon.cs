using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>The app's icon (Assets\app.ico, all sizes) and logo (Assets\app.png), built into the .exe.</summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon> _icon = new(() => Load(s => new Icon(s)));
    private static readonly Lazy<Image> _logo = new(() => Load(s => Image.FromStream(s)));

    /// <summary>For window title bars and the taskbar. Null only if the resource is missing.</summary>
    public static Icon Icon => _icon.Value;

    /// <summary>256 px logo for drawing (e.g. on the splash).</summary>
    public static Image Logo => _logo.Value;

    private static T Load<T>(Func<Stream, T> make) where T : class
    {
        string name = typeof(T) == typeof(Icon) ? "SwitchboardRM.app.ico" : "SwitchboardRM.app.png";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return null;
            // copy out, because Image.FromStream needs its stream for the image's whole life
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return make(copy);
        }
        catch { return null; }
    }
}

/// <summary>
/// Keeps Switchboard's icon on the taskbar. The taskbar re-asks each window for its icon now and then (after
/// unlocking, waking, display changes, Explorer refreshing). If the app is busy at that moment it falls back to the
/// icon of the window's class, which WinForms leaves empty, so the generic app icon showed. This sets the class icon
/// too (so the fallback is right) and re-sends the window icons when called again.
/// </summary>
internal static class TaskbarIcon
{
    private static Icon _big, _small;   // kept alive: Windows uses the handles directly

    public static void Apply(Form form)
    {
        if (AppIcon.Icon == null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            var bigSize = SystemInformation.IconSize;        // 32 px at 100 %, larger with display scaling
            var smallSize = SystemInformation.SmallIconSize;  // 16 px at 100 %
            var big = new Icon(AppIcon.Icon, bigSize);
            var small = new Icon(AppIcon.Icon, smallSize);

            SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_BIG, big.Handle);
            SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_SMALL, small.Handle);
            SetClassLongPtr(form.Handle, GCLP_HICON, big.Handle);
            SetClassLongPtr(form.Handle, GCLP_HICONSM, small.Handle);

            // swap in the new ones only once Windows has them, then let the old ones go
            var (oldBig, oldSmall) = (_big, _small);
            (_big, _small) = (big, small);
            oldBig?.Dispose();
            oldSmall?.Dispose();
        }
        catch (Exception ex) { Log.Write("Couldn't set the taskbar icon: " + ex.Message); }
    }

    private const int WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1;
    private const int GCLP_HICON = -14, GCLP_HICONSM = -34;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int index, IntPtr value);
}
