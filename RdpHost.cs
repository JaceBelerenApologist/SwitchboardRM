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
/// The RDP client's non-scriptable interface with SendKeys (for Send Ctrl+Alt+Del). COM calls by slot position, so every
/// method of the base interface (IMsTscNonScriptable) is declared first, in its order, then this interface's two.
/// </summary>
[ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable
{
    // IMsTscNonScriptable
    void PutClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
    void PutPortablePassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortablePassword();
    void PutPortableSalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortableSalt();
    void PutBinaryPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinaryPassword();
    void PutBinarySalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinarySalt();
    void ResetPassword();
    // IMsRdpClientNonScriptable
    void NotifyRedirectDeviceChange(IntPtr wParam, IntPtr lParam);
    /// <summary>pbArrayKeyUp: VARIANT_BOOL[] (0 = down, -1 = up); plKeyData: int[] of scan codes.</summary>
    void SendKeys(int numKeys, IntPtr pbArrayKeyUp, IntPtr plKeyData);
}

/// <summary>
/// The RDP control's events interface. Only the event Switchboard needs is declared; the control's other events
/// are simply not handled.
/// </summary>
[ComVisible(true), Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    /// <summary>The session ended or was lost; discReason says how (1 = this side disconnected, 2 = the remote user, 3 = the server ...).</summary>
    [DispId(4)] void OnDisconnected(int discReason);

    /// <summary>Ctrl+Alt+Left or Ctrl+Alt+Right was pressed inside the session (the control gives up the keyboard).</summary>
    [DispId(23)] void OnFocusReleased(int iDirection);
}

/// <summary>Receives the RDP control's events and passes them on.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IMsTscAxEvents
{
    private readonly Action<int> _focusReleased, _disconnected;
    public RdpEventSink(Action<int> focusReleased, Action<int> disconnected) { _focusReleased = focusReleased; _disconnected = disconnected; }
    public void OnFocusReleased(int iDirection) => _focusReleased?.Invoke(iDirection);
    public void OnDisconnected(int discReason) => _disconnected?.Invoke(discReason);
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

    /// <summary>
    /// Ctrl+Alt+Left (negative) or Ctrl+Alt+Right (positive) was pressed inside the session: the only keys the
    /// control hands back, used to move between tabs. Raised on the UI thread.
    /// </summary>
    public event Action<int> FocusReleased;

    /// <summary>
    /// The session ended or was lost: the control's reason and extended reason (see MainForm.IsDeliberateEnd). Raised on
    /// the UI thread.
    /// </summary>
    public event Action<int, int> Disconnected;
    private ConnectionPointCookie _events;

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
        // Ctrl+Alt+Left / Ctrl+Alt+Right hand the keyboard back to Switchboard (next / previous tab)
        Try(() => Set(adv, "HotKeyFocusReleaseLeft", 0x25));   // VK_LEFT
        Try(() => Set(adv, "HotKeyFocusReleaseRight", 0x27));  // VK_RIGHT

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

    /// <summary>Subscribe to the control's events once it exists.</summary>
    protected override void CreateSink()
    {
        try
        {
            var sink = new RdpEventSink(direction =>
            {
                Log.Write($"RDP released the keyboard (direction {direction})");
                if (!IsDisposed && IsHandleCreated) BeginInvoke(() => FocusReleased?.Invoke(direction));
            }, reason =>
            {
                int extended = 0;
                try { if (GetOcx() is { } rdp) extended = Convert.ToInt32(Get(rdp, "ExtendedDisconnectReason")); } catch { }
                Log.Write($"RDP disconnected: reason {reason}, extended reason {extended}");
                if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Disconnected?.Invoke(reason, extended));
            });
            _events = new ConnectionPointCookie(GetOcx(), sink, typeof(IMsTscAxEvents));
        }
        catch (Exception ex) { Log.Write("RDP control events aren't available: " + ex.Message); }
    }

    protected override void DetachSink()
    {
        try { _events?.Disconnect(); } catch { }
        _events = null;
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

    /// <summary>
    /// Ctrl+Alt+Del inside the session (the keys themselves always go to this PC; Ctrl+Alt+End does the same from the
    /// keyboard): Ctrl, Alt and Delete down, then up, as scan codes.
    /// </summary>
    public void SendCtrlAltDel()
    {
        if (GetOcx() is not IMsRdpClientNonScriptable keys)
            throw new InvalidOperationException("this Remote Desktop control can't send keys");
        Focus();
        short[] up = { 0, 0, 0, -1, -1, -1 };                    // VARIANT_BOOL: false = pressed, true (-1) = released
        int[] scan = { 0x1D, 0x38, 0x53, 0x53, 0x38, 0x1D };     // Ctrl, Alt, Delete ... Delete, Alt, Ctrl
        var upHandle = GCHandle.Alloc(up, GCHandleType.Pinned);
        var scanHandle = GCHandle.Alloc(scan, GCHandleType.Pinned);
        try { keys.SendKeys(scan.Length, upHandle.AddrOfPinnedObject(), scanHandle.AddrOfPinnedObject()); }
        finally { upHandle.Free(); scanHandle.Free(); }
        Log.Write("Sent Ctrl+Alt+Del to the remote session");
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
