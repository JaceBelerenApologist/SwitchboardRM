using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwitchboardRM;

/// <summary>The one RDP client method that has to be called through a plain COM interface.</summary>
[ComImport, Guid("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    string ClearTextPassword { [param: MarshalAs(UnmanagedType.BStr)] set; }
}

/// <summary>
/// Hosts the Windows Remote Desktop ActiveX control (mstscax.dll), the same one mstsc.exe and RDCMan use.
/// Properties are set by name through COM so no generated interop assembly is needed.
/// </summary>
internal sealed class RdpHost : AxHost
{
    // MsRdpClient9/8/7/6 "NotSafeForScripting" classes, newest first.
    private static readonly string[] Clsids =
    {
        "8b918b82-7985-4c24-89df-c33ad2bbfbcd",
        "4eb5335b-6429-477d-b922-e06a28ecd8bf",
        "54d38bf7-b1ef-4479-9674-1bd6ea465258",
        "7584c670-2274-4efb-b00b-d6aaba6d3850",
    };

    private static readonly string[] AdvancedNames =
    {
        "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6",
        "AdvancedSettings5", "AdvancedSettings4", "AdvancedSettings3", "AdvancedSettings2",
    };

    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 400 };
    private Size _lastSent;
    private bool _scaling;

    public RdpHost(string clsid) : base(clsid)
    {
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); SyncSize(); };
    }

    /// <summary>The newest RDP client control registered on this PC, or null.</summary>
    public static string FindClsid()
    {
        foreach (var c in Clsids)
        {
            using var key = Registry.ClassesRoot.OpenSubKey($"CLSID\\{{{c}}}");
            if (key != null) return c;
        }
        return null;
    }

    /// <summary>Configure the control and start connecting.</summary>
    /// <param name="direct">A plain connection straight to the server (not through an AWS tunnel on localhost).</param>
    public void Start(string server, int port, string user, string password, bool direct = false)
    {
        object rdp = GetOcx() ?? throw new InvalidOperationException("The RDP control is not ready yet.");
        var size = DesktopSize(ClientSize);

        Set(rdp, "Server", server);

        var (domain, name) = SplitUser(user);
        if (name.Length > 0) Set(rdp, "UserName", name);
        if (domain.Length > 0) Try(() => Set(rdp, "Domain", domain));

        Try(() => Set(rdp, "DesktopWidth", size.Width));
        Try(() => Set(rdp, "DesktopHeight", size.Height));
        Try(() => Set(rdp, "ColorDepth", 32));

        object adv = FirstAdvanced(rdp);
        Set(adv, "RDPPort", port);
        Try(() => Set(adv, "SmartSizing", false)); // resized for real by SyncSize; scaling is only the fallback
        Try(() => Set(adv, "EnableCredSspSupport", true));
        // Tunnel: the certificate is for the server, never "localhost", so don't check it.
        // Direct: check it and warn if it can't be verified, like mstsc does.
        Try(() => Set(adv, "AuthenticationLevel", direct ? 2u : 0u));
        Try(() => Set(adv, "RedirectClipboard", true));

        if (!string.IsNullOrEmpty(password))
        {
            ((IMsTscNonScriptable)rdp).ClearTextPassword = password;
        }
        else
        {
            Try(() => Set(adv, "PromptForCredsOnClient", true));
            Try(() => Set(adv, "PromptForCredentials", true));
        }

        Call(rdp, "Connect");
        _lastSent = size;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // wait until the user stops dragging, then change the remote resolution once
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>
    /// Change the remote desktop's resolution to fit this control (needs Windows Server 2012 R2 / Windows 8.1 or newer
    /// on the remote side). If the session can't do that, the picture is scaled to fit instead.
    /// </summary>
    public void SyncSize()
    {
        if (ClientSize.Width < 200 || ClientSize.Height < 200) return; // minimised or hidden
        object rdp;
        try
        {
            rdp = GetOcx();
            if (rdp == null || Convert.ToInt32(Get(rdp, "Connected")) != 1) return;
        }
        catch { return; }

        var size = DesktopSize(ClientSize);
        if (size == _lastSent && !_scaling) return;

        try
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            uint mmW = (uint)Math.Round(size.Width * 25.4 / dpi), mmH = (uint)Math.Round(size.Height * 25.4 / dpi);
            rdp.GetType().InvokeMember("UpdateSessionDisplaySettings", BindingFlags.InvokeMethod, null, rdp, new object[]
            {
                (uint)size.Width, (uint)size.Height,  // desktop size in pixels
                mmW, mmH,                             // physical size in millimetres
                0u,                                   // landscape
                100u, 100u,                           // desktop and device scale factors
            });
            _lastSent = size;
            if (_scaling) SetScaling(rdp, false);
        }
        catch (Exception ex)
        {
            Log.Write("Dynamic resize not accepted, scaling instead: " + (ex.InnerException ?? ex).Message);
            if (!_scaling) SetScaling(rdp, true);
        }
    }

    private void SetScaling(object rdp, bool on)
    {
        try { Set(FirstAdvanced(rdp), "SmartSizing", on); _scaling = on; } catch { }
    }

    /// <summary>The remote side accepts 200-8192 pixels each way, with an even width.</summary>
    private static Size DesktopSize(Size client) =>
        new(Math.Clamp(client.Width, 200, 8192) & ~1, Math.Clamp(client.Height, 200, 8192));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _resizeTimer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>0 = disconnected, 1 = connected, 2 = connecting.</summary>
    public int ConnectionState()
    {
        object rdp = GetOcx();
        return rdp == null ? 0 : Convert.ToInt32(Get(rdp, "Connected"));
    }

    public void SafeDisconnect()
    {
        try
        {
            object rdp = GetOcx();
            if (rdp != null && Convert.ToInt32(Get(rdp, "Connected")) != 0) Call(rdp, "Disconnect");
        }
        catch { }
    }

    private static object FirstAdvanced(object rdp)
    {
        foreach (var n in AdvancedNames)
        {
            try
            {
                var o = Get(rdp, n);
                if (o != null) return o;
            }
            catch { }
        }
        throw new InvalidOperationException("This PC's Remote Desktop control has no advanced settings.");
    }

    private static (string Domain, string Name) SplitUser(string user)
    {
        user = (user ?? "").Trim();
        int i = user.IndexOf('\\');
        return i > 0 ? (user[..i], user[(i + 1)..]) : ("", user);
    }

    private static object Get(object o, string name) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);

    private static void Set(object o, string name, object value) =>
        o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });

    private static void Call(object o, string name) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, null);

    private static void Try(Action a)
    {
        try { a(); } catch { /* optional setting not supported by this Windows version */ }
    }
}
