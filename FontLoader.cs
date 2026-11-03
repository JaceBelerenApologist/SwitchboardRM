using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SwitchboardRM;

/// <summary>One font file from the fonts folder: its family, weight (100-900) and whether it's italic.</summary>
internal sealed record LoadedFont(string Path, string Family, int Weight, bool Italic);

/// <summary>
/// Fonts in a "fonts" folder next to the .exe (while developing: the project's own fonts\ folder), loaded at start-up
/// for Switchboard only: nothing is installed in Windows. The app's text can use them like installed fonts
/// (Theme.CreateFont); the terminals get the files themselves, since their browser can't see them otherwise.
/// </summary>
internal static class FontLoader
{
    private static readonly List<LoadedFont> _fonts = new();
    private static readonly List<PrivateFontCollection> _collections = new();   // kept alive while the app runs
    private static readonly Dictionary<string, FontFamily> _families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Extensions = { ".ttf", ".otf", ".ttc" };

    public static IReadOnlyList<LoadedFont> Fonts => _fonts;
    public static IEnumerable<string> Families => _families.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>fonts\ next to the .exe, or (running from bin\ while developing) the project folder's fonts\.</summary>
    public static string Dir
    {
        get
        {
            string here = Path.Combine(AppContext.BaseDirectory, "fonts");
            if (Directory.Exists(here)) return here;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "fonts");
                if (Directory.Exists(candidate) && dir.GetFiles("*.csproj").Length > 0) return candidate;
            }
            return here;
        }
    }

    /// <summary>Load every font file in the folder (once, at start-up). Files that can't be read are skipped and logged.</summary>
    public static void LoadAll()
    {
        string dir = Dir;
        if (!Directory.Exists(dir)) return;

        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                // for text drawn by Windows (GDI): available to this app only
                AddFontResourceEx(path, FR_PRIVATE, IntPtr.Zero);

                // for fonts created by .NET (GDI+), and to learn the family name
                var collection = new PrivateFontCollection();
                collection.AddFontFile(path);
                _collections.Add(collection);

                var (weight, italic) = StyleFromName(Path.GetFileNameWithoutExtension(path));
                foreach (var family in collection.Families)
                {
                    if (!_families.ContainsKey(family.Name)) _families[family.Name] = family;
                    _fonts.Add(new LoadedFont(path, family.Name, weight, italic));
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Font {Path.GetFileName(path)} couldn't be loaded: {ex.Message}");
            }
        }
        if (_fonts.Count > 0)
            Log.Write($"Loaded {_fonts.Count} font file(s) from {dir}: {string.Join(", ", Families)}");
    }

    public static bool TryGetFamily(string name, out FontFamily family) => _families.TryGetValue(name ?? "", out family);

    /// <summary>The font files of one family from the folder (for the terminal).</summary>
    public static List<LoadedFont> FilesOf(string family) =>
        _fonts.Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>"JetBrainsMono-BoldItalic" -> (700, true). Font families name their files this way.</summary>
    private static (int Weight, bool Italic) StyleFromName(string name)
    {
        string n = name.ToLowerInvariant();
        bool italic = n.Contains("italic") || n.Contains("oblique");
        int weight =
            Regex.IsMatch(n, "extra-?bold|ultra-?bold") ? 800 :
            Regex.IsMatch(n, "semi-?bold|demi-?bold") ? 600 :
            Regex.IsMatch(n, "black|heavy") ? 900 :
            n.Contains("bold") ? 700 :
            n.Contains("medium") ? 500 :
            Regex.IsMatch(n, "extra-?light|ultra-?light") ? 200 :
            n.Contains("light") ? 300 :
            n.Contains("thin") ? 100 :
            400;
        return (weight, italic);
    }

    private const uint FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
}
