using System.Xml.Linq;
using Microsoft.Win32;

namespace SwitchboardRM;

/// <summary>
/// Reads connections from other remote-desktop tools into saved connections. Only the basics are taken: address,
/// port, user, name, group and protocol. Passwords are not imported (each tool encrypts them its own way).
/// </summary>
internal static class ConnectionImport
{
    public sealed class Result
    {
        public List<SavedConnection> Found { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    // ---------------------------------------------------------------- RDCMan (.rdg)

    /// <summary>Remote Desktop Connection Manager files: 2.7+ (name under &lt;properties&gt;) and 2.2 (name directly on the server).</summary>
    public static Result FromRdcMan(string path)
    {
        var r = new Result();
        var doc = XDocument.Load(path);
        string fileName = Path.GetFileNameWithoutExtension(path);

        foreach (var server in doc.Descendants().Where(e => e.Name.LocalName == "server"))
        {
            string host = Prop(server, "name");
            if (string.IsNullOrWhiteSpace(host)) continue;
            string display = Prop(server, "displayName");

            // group path from the enclosing groups, outermost first ("Datacenter / SQL")
            var groups = server.Ancestors().Where(a => a.Name.LocalName == "group")
                .Select(g => Prop(g, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Reverse().ToList();

            // a user set on the server itself; inherited credentials live on groups and aren't followed
            string user = "";
            var creds = Child(server, "logonCredentials");
            if (creds != null && !string.Equals((string)creds.Attribute("inherit"), "FromParent", StringComparison.OrdinalIgnoreCase))
                user = JoinUser(Child(creds, "domain")?.Value, Child(creds, "userName")?.Value);

            int port = int.TryParse(Child(Child(server, "connectionSettings"), "port")?.Value, out int p) && p > 0 ? p : 0;
            var (h, embeddedPort) = ConnectionStore.ParseAddress(host, 3389);

            r.Found.Add(new SavedConnection
            {
                Protocol = "RDP",
                Name = string.IsNullOrWhiteSpace(display) || display == host ? "" : display.Trim(),
                Host = h,
                Port = port > 0 ? port : embeddedPort,
                User = user,
                Group = groups.Count > 0 ? string.Join(" / ", groups) : fileName,
                Description = Prop(server, "comment").Trim(),
            });
        }
        if (r.Found.Count == 0) r.Warnings.Add("No servers were found in this file. Is it an RDCMan .rdg file?");
        return r;
    }

    /// <summary>RDCMan 2.7 keeps values under &lt;properties&gt;; 2.2 puts them straight on the element.</summary>
    private static string Prop(XElement e, string name) =>
        Child(Child(e, "properties"), name)?.Value ?? Child(e, name)?.Value ?? "";

    private static XElement Child(XElement e, string localName) =>
        e?.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    // ---------------------------------------------------------------- .rdp files

    public static Result FromRdpFiles(IEnumerable<string> paths)
    {
        var r = new Result();
        foreach (var path in paths)
        {
            try
            {
                string address = "", user = "";
                int port = 0;
                foreach (var raw in File.ReadAllLines(path)) // .rdp files are usually UTF-16; the BOM tells us
                {
                    var parts = raw.Split(':', 3);
                    if (parts.Length < 3) continue;
                    switch (parts[0].Trim().ToLowerInvariant())
                    {
                        case "full address": address = parts[2].Trim(); break;
                        case "username": user = parts[2].Trim(); break;
                        case "server port": int.TryParse(parts[2], out port); break;
                    }
                }
                if (address.Length == 0) { r.Warnings.Add($"{Path.GetFileName(path)}: no computer address in the file"); continue; }

                var (host, embeddedPort) = ConnectionStore.ParseAddress(address, 3389);
                r.Found.Add(new SavedConnection
                {
                    Protocol = "RDP",
                    Name = Path.GetFileNameWithoutExtension(path),
                    Host = host,
                    Port = embeddedPort != 3389 ? embeddedPort : port > 0 ? port : 3389,
                    User = user,
                    Group = "Imported .rdp files",
                });
            }
            catch (Exception ex) { r.Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        return r;
    }

    // ---------------------------------------------------------------- mRemoteNG (confCons.xml)

    public static Result FromMRemoteNg(string path)
    {
        var r = new Result();
        var doc = XDocument.Load(path);
        if (string.Equals((string)doc.Root?.Attribute("FullFileEncryption"), "true", StringComparison.OrdinalIgnoreCase))
        {
            r.Warnings.Add("This file is fully encrypted. In mRemoteNG, turn off \"Encrypt complete connection file\" " +
                           "(Options > Security), save, and import again.");
            return r;
        }

        int skipped = 0;
        foreach (var node in doc.Descendants().Where(e => e.Name.LocalName == "Node"))
        {
            if (!string.Equals((string)node.Attribute("Type"), "Connection", StringComparison.OrdinalIgnoreCase)) continue;
            string protocol = ((string)node.Attribute("Protocol") ?? "").ToUpperInvariant();
            bool ssh = protocol is "SSH2" or "SSH1";
            if (protocol != "RDP" && !ssh) { skipped++; continue; } // VNC, Telnet, HTTP ... aren't supported here

            string host = ((string)node.Attribute("Hostname") ?? "").Trim();
            if (host.Length == 0) continue;
            int port = int.TryParse((string)node.Attribute("Port"), out int p) && p > 0 ? p : ssh ? 22 : 3389;

            var folders = node.Ancestors().Where(a => a.Name.LocalName == "Node" &&
                    string.Equals((string)a.Attribute("Type"), "Container", StringComparison.OrdinalIgnoreCase))
                .Select(a => (string)a.Attribute("Name")).Where(n => !string.IsNullOrWhiteSpace(n)).Reverse().ToList();
            string name = ((string)node.Attribute("Name") ?? "").Trim();

            r.Found.Add(new SavedConnection
            {
                Protocol = ssh ? "SSH" : "RDP",
                Name = name == host ? "" : name,
                Host = host,
                Port = port,
                User = ssh ? ((string)node.Attribute("Username") ?? "").Trim()
                           : JoinUser((string)node.Attribute("Domain"), (string)node.Attribute("Username")),
                Group = folders.Count > 0 ? string.Join(" / ", folders) : "mRemoteNG",
                Description = ((string)node.Attribute("Descr") ?? (string)node.Attribute("Description") ?? "").Trim(),
            });
        }
        if (skipped > 0) r.Warnings.Add($"{skipped} connection(s) using other protocols (VNC, Telnet, web ...) were skipped.");
        if (r.Found.Count == 0 && skipped == 0) r.Warnings.Add("No connections were found. Is this mRemoteNG's confCons.xml?");
        return r;
    }

    // ---------------------------------------------------------------- PuTTY (registry)

    public static Result FromPutty()
    {
        var r = new Result();
        using var sessions = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (sessions == null)
        {
            r.Warnings.Add("No PuTTY saved sessions were found for this Windows user.");
            return r;
        }
        foreach (var keyName in sessions.GetSubKeyNames())
        {
            using var s = sessions.OpenSubKey(keyName);
            if (s == null) continue;
            string protocol = (s.GetValue("Protocol") as string ?? "ssh").ToLowerInvariant();
            string host = (s.GetValue("HostName") as string ?? "").Trim();
            if (protocol != "ssh" || host.Length == 0) continue; // telnet/serial/raw, or the empty "Default Settings"

            string user = (s.GetValue("UserName") as string ?? "").Trim();
            int at = host.LastIndexOf('@'); // PuTTY also accepts "user@host" in the host box
            if (at > 0) { if (user.Length == 0) user = host[..at]; host = host[(at + 1)..]; }
            int port = s.GetValue("PortNumber") is int p && p > 0 ? p : 22;

            string name = Uri.UnescapeDataString(keyName); // PuTTY stores "My%20Server"
            r.Found.Add(new SavedConnection
            {
                Protocol = "SSH",
                Name = name == host ? "" : name,
                Host = host,
                Port = port,
                User = user,
                Group = "PuTTY",
            });
        }
        if (r.Found.Count == 0) r.Warnings.Add("No PuTTY SSH sessions with a host name were found.");
        return r;
    }

    // ----------------------------------------------------------------

    /// <summary>"DOMAIN" + "user" -> "DOMAIN\user" (unless the user name already includes a domain).</summary>
    private static string JoinUser(string domain, string user)
    {
        user = (user ?? "").Trim();
        domain = (domain ?? "").Trim();
        if (user.Length == 0) return "";
        if (domain.Length == 0 || user.Contains('\\') || user.Contains('@')) return user;
        return $"{domain}\\{user}";
    }
}
