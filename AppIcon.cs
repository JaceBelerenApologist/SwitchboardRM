using System.Drawing;
using System.Reflection;

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
