using System.Drawing;
using System.Text;

namespace SwitchboardRM;

/// <summary>
/// A terminal colour scheme: background, text, cursor, selection, the 16 ANSI colours every console program uses,
/// and the colours PowerShell itself uses for its prompt, syntax and output ("roles").
/// </summary>
internal sealed class TerminalScheme
{
    public string Name { get; init; }
    public string Background { get; init; }
    public string Foreground { get; init; }
    public string Cursor { get; init; }
    public string Selection { get; init; }
    /// <summary>black, red, green, yellow, blue, magenta, cyan, white, then the 8 bright versions.</summary>
    public string[] Ansi { get; init; }
    /// <summary>PowerShell colours by role (Command, Parameter, String, Directory ...). Missing roles come from Ansi.</summary>
    public Dictionary<string, string> Roles { get; init; } = new();

    public static readonly string[] AnsiNames =
    {
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    };

    /// <summary>The colour for a PowerShell role: the scheme's own choice, or a sensible ANSI colour.</summary>
    public string Role(string role)
    {
        if (Roles.TryGetValue(role, out var c)) return c;
        return role switch
        {
            "Command" => Ansi[2],           // green
            "Parameter" => Ansi[3],         // yellow
            "String" => Ansi[6],            // cyan
            "Variable" => Ansi[5],          // magenta
            "Number" => Ansi[12],           // bright blue
            "Operator" => Ansi[13],         // bright magenta
            "Keyword" => Ansi[5],
            "Type" => Ansi[4],              // blue
            "Member" => Foreground,
            "Comment" => Ansi[8],           // bright black
            "Error" => Ansi[1],             // red
            "Warning" => Ansi[3],
            "Verbose" => Ansi[6],
            "Debug" => Ansi[8],
            "Directory" => Ansi[12],
            "Link" => Ansi[6],
            "Executable" => Ansi[2],
            "TableHeader" => Ansi[12],
            "Accent" => Ansi[13],           // list property names, error highlights
            "PromptLabel" => Ansi[8],       // the "PS"
            "PromptPath" => Ansi[12],
            "PromptArrow" => Ansi[13],
            _ => Foreground,
        };
    }
}

internal static class TerminalSchemes
{
    /// <summary>Every scheme, in the order Settings lists them ("Match app" is the absence of one).</summary>
    public static readonly TerminalScheme[] All =
    {
        new()
        {
            Name = "Dracula", Background = "#282A36", Foreground = "#F8F8F2", Cursor = "#F8F8F2", Selection = "#44475A",
            Ansi = new[] { "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                           "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF" },
            // the same mapping as a hand-made Dracula PowerShell profile
            Roles = new()
            {
                ["Command"] = "#50FA7B", ["Parameter"] = "#FFB86C", ["String"] = "#F1FA8C", ["Variable"] = "#BD93F9",
                ["Number"] = "#BD93F9", ["Operator"] = "#FF79C6", ["Keyword"] = "#FF79C6", ["Type"] = "#8BE9FD",
                ["Member"] = "#8BE9FD", ["Comment"] = "#6272A4", ["Error"] = "#FF5555", ["Warning"] = "#FFB86C",
                ["Verbose"] = "#8BE9FD", ["Debug"] = "#6272A4", ["Directory"] = "#BD93F9", ["Link"] = "#8BE9FD",
                ["Executable"] = "#50FA7B", ["TableHeader"] = "#BD93F9", ["Accent"] = "#FF79C6",
                ["PromptLabel"] = "#6272A4", ["PromptPath"] = "#BD93F9", ["PromptArrow"] = "#FF79C6",
            },
        },
        new()
        {
            Name = "Campbell", Background = "#0C0C0C", Foreground = "#CCCCCC", Cursor = "#FFFFFF", Selection = "#3A3A3A",
            Ansi = new[] { "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                           "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2" },
        },
        new()
        {
            Name = "One Half Dark", Background = "#282C34", Foreground = "#DCDFE4", Cursor = "#DCDFE4", Selection = "#474E5D",
            Ansi = new[] { "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
                           "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4" },
        },
        new()
        {
            Name = "One Half Light", Background = "#FAFAFA", Foreground = "#383A42", Cursor = "#4F525D", Selection = "#DCDCE0",
            Ansi = new[] { "#383A42", "#E45649", "#50A14F", "#C18401", "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
                           "#4F525D", "#DF6C75", "#98C379", "#E4C07A", "#61AFEF", "#C577DD", "#56B5C1", "#FFFFFF" },
            Roles = new() { ["Directory"] = "#0184BC", ["TableHeader"] = "#0184BC", ["PromptPath"] = "#0184BC",
                            ["Number"] = "#0184BC", ["Operator"] = "#A626A4", ["Accent"] = "#A626A4", ["PromptArrow"] = "#A626A4" },
        },
        new()
        {
            Name = "Solarized Dark", Background = "#002B36", Foreground = "#839496", Cursor = "#93A1A1", Selection = "#073642",
            Ansi = new[] { "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                           "#586E75", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3" },
            Roles = new() { ["Directory"] = "#268BD2", ["TableHeader"] = "#268BD2", ["PromptPath"] = "#268BD2",
                            ["Number"] = "#6C71C4", ["Operator"] = "#D33682", ["Accent"] = "#D33682", ["PromptArrow"] = "#D33682" },
        },
        new()
        {
            Name = "Solarized Light", Background = "#FDF6E3", Foreground = "#657B83", Cursor = "#586E75", Selection = "#EEE8D5",
            Ansi = new[] { "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                           "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3" },
            Roles = new() { ["Directory"] = "#268BD2", ["TableHeader"] = "#268BD2", ["PromptPath"] = "#268BD2",
                            ["Number"] = "#6C71C4", ["Operator"] = "#D33682", ["Accent"] = "#D33682", ["PromptArrow"] = "#D33682",
                            ["Comment"] = "#93A1A1", ["PromptLabel"] = "#93A1A1", ["Debug"] = "#93A1A1" },
        },
        new()
        {
            Name = "Nord", Background = "#2E3440", Foreground = "#D8DEE9", Cursor = "#D8DEE9", Selection = "#434C5E",
            Ansi = new[] { "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
                           "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4" },
            Roles = new() { ["Directory"] = "#88C0D0", ["PromptPath"] = "#88C0D0", ["TableHeader"] = "#81A1C1" },
        },
        new()
        {
            Name = "Gruvbox Dark", Background = "#282828", Foreground = "#EBDBB2", Cursor = "#EBDBB2", Selection = "#504945",
            Ansi = new[] { "#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984",
                           "#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2" },
            Roles = new() { ["Parameter"] = "#FE8019", ["Warning"] = "#FE8019" },
        },
        new()
        {
            Name = "Tokyo Night", Background = "#1A1B26", Foreground = "#C0CAF5", Cursor = "#C0CAF5", Selection = "#33467C",
            Ansi = new[] { "#15161E", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
                           "#414868", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5" },
            Roles = new() { ["Parameter"] = "#FF9E64", ["Warning"] = "#FF9E64", ["Directory"] = "#7AA2F7" },
        },
    };

    /// <summary>The colours terminals use with "Match app": the app's background, and Campbell / One Half Light.</summary>
    public static TerminalScheme MatchApp()
    {
        var p = Theme.Current;
        return p.IsDark
            ? new TerminalScheme
            {
                Name = "Match app", Background = ColorMath.Hex(p.Back), Foreground = "#E7E7EC", Cursor = ColorMath.Hex(p.Accent),
                Selection = ColorMath.Hex(ColorMath.Mix(p.Accent, p.Back, 0.6)),
                Ansi = All.First(s => s.Name == "Campbell").Ansi,
            }
            : new TerminalScheme
            {
                Name = "Match app", Background = ColorMath.Hex(p.Surface), Foreground = "#383A42", Cursor = ColorMath.Hex(p.Accent),
                Selection = ColorMath.Hex(ColorMath.Mix(p.Accent, p.Surface, 0.75)),
                Ansi = new[] { "#383A42", "#E45649", "#50A14F", "#C18301", "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
                               "#4F525D", "#DF6C75", "#98C379", "#E4C07A", "#61AFEF", "#C577DD", "#56B5C1", "#FFFFFF" },
            };
    }

    /// <summary>The scheme with this name, or null for "Match app" (the default).</summary>
    public static TerminalScheme Find(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// PowerShell commands that apply the scheme's colours: $PSStyle (PowerShell 7) for file lists, tables, errors and
    /// progress; PSReadLine for the colours as you type; and the prompt, but only if it's still PowerShell's own.
    /// Written for both PowerShell 7 and Windows PowerShell 5.1 (which skips what it doesn't have).
    /// </summary>
    public static string PowerShellStyling(TerminalScheme s)
    {
        string Esc(string hex)
        {
            var c = ColorTranslator.FromHtml(hex);
            return $"\"$([char]27)[38;2;{c.R};{c.G};{c.B}m\"";
        }
        var bold = "\"$([char]27)[1m\"";
        var sel = ColorTranslator.FromHtml(s.Selection);

        var sb = new StringBuilder();
        sb.AppendLine($"# Switchboard: terminal colour scheme \"{s.Name}\" (Settings > Appearance > Terminal colours)");
        sb.AppendLine("if ($PSStyle) {");
        sb.AppendLine($"  $PSStyle.FileInfo.Directory = {bold} + {Esc(s.Role("Directory"))}");
        sb.AppendLine($"  $PSStyle.FileInfo.SymbolicLink = {Esc(s.Role("Link"))}");
        sb.AppendLine($"  $PSStyle.FileInfo.Executable = {Esc(s.Role("Executable"))}");
        sb.AppendLine($"  $PSStyle.Formatting.TableHeader = {bold} + {Esc(s.Role("TableHeader"))}");
        sb.AppendLine($"  $PSStyle.Formatting.FormatAccent = {Esc(s.Role("Accent"))}");
        sb.AppendLine($"  $PSStyle.Formatting.ErrorAccent = {Esc(s.Role("Accent"))}");
        sb.AppendLine($"  $PSStyle.Formatting.Error = {Esc(s.Role("Error"))}");
        sb.AppendLine($"  $PSStyle.Formatting.Warning = {Esc(s.Role("Warning"))}");
        sb.AppendLine($"  $PSStyle.Formatting.Verbose = {Esc(s.Role("Verbose"))}");
        sb.AppendLine($"  $PSStyle.Formatting.Debug = {Esc(s.Role("Debug"))}");
        sb.AppendLine($"  $PSStyle.Progress.Style = {Esc(s.Role("TableHeader"))}");
        sb.AppendLine("}");

        // PSReadLine: one colour at a time, so an older PSReadLine (Windows PowerShell 5.1) just skips what it doesn't know
        sb.AppendLine("if (Get-Command Set-PSReadLineOption -ErrorAction SilentlyContinue) {");
        sb.AppendLine("  $sbColors = @{");
        sb.AppendLine($"    Default = {Esc(s.Foreground)}; Command = {Esc(s.Role("Command"))}; Parameter = {Esc(s.Role("Parameter"))}");
        sb.AppendLine($"    String = {Esc(s.Role("String"))}; Variable = {Esc(s.Role("Variable"))}; Number = {Esc(s.Role("Number"))}");
        sb.AppendLine($"    Operator = {Esc(s.Role("Operator"))}; Keyword = {Esc(s.Role("Keyword"))}; Type = {Esc(s.Role("Type"))}");
        sb.AppendLine($"    Member = {Esc(s.Role("Member"))}; Comment = {Esc(s.Role("Comment"))}; Error = {Esc(s.Role("Error"))}");
        sb.AppendLine($"    Emphasis = {Esc(s.Role("Accent"))}; InlinePrediction = {Esc(s.Role("Comment"))}; ContinuationPrompt = {Esc(s.Role("Comment"))}");
        sb.AppendLine($"    Selection = \"$([char]27)[48;2;{sel.R};{sel.G};{sel.B}m\"");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($sbKey in $sbColors.Keys) { try { Set-PSReadLineOption -Colors @{ $sbKey = $sbColors[$sbKey] } } catch { } }");
        sb.AppendLine("  Remove-Variable sbColors, sbKey -ErrorAction SilentlyContinue");
        sb.AppendLine("}");

        // the prompt: only replace PowerShell's built-in one, never one from your profile
        sb.AppendLine("$sbPrompt = (Get-Command prompt -CommandType Function -ErrorAction SilentlyContinue).Definition");
        sb.AppendLine("if ($sbPrompt -like '*SessionState.Path.CurrentLocation*') {");
        sb.AppendLine("  function global:prompt { " +
                      $"{Esc(s.Role("PromptLabel"))} + 'PS ' + {Esc(s.Role("PromptPath"))} + $PWD.Path + {Esc(s.Role("PromptArrow"))} + ' >' + \"$([char]27)[0m \" }}");
        sb.AppendLine("}");
        sb.AppendLine("Remove-Variable sbPrompt -ErrorAction SilentlyContinue");
        return sb.ToString();
    }

    /// <summary>The styling as a -EncodedCommand argument (not a script file, so execution policy doesn't apply).</summary>
    public static string EncodedStyling(TerminalScheme s) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellStyling(s)));
}
