using System.Runtime.InteropServices;

namespace SwitchboardRM;

/// <summary>
/// "Allow only one copy of Switchboard": a named mutex marks the running copy; a second start (when the setting is on)
/// signals it to come to the front, then exits. Both names are per Windows session ("Local\"), so on a shared server
/// each signed-in user has their own copy.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\SwitchboardRM.Running";
    private const string ActivateName = @"Local\SwitchboardRM.Activate";

    private static Mutex _mutex;
    private static EventWaitHandle _activate;

    /// <summary>Raised (on a thread-pool thread) when another start asks this copy to come to the front.</summary>
    public static event Action ActivateRequested;

    /// <summary>
    /// Claim the "running" mark. False if another copy already has it. After an update (--updated) the old copy may
    /// still be closing, so wait a little for it first.
    /// </summary>
    public static bool Claim(bool afterUpdate)
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (first) return true;
        if (!afterUpdate) return false;
        try { return _mutex.WaitOne(TimeSpan.FromSeconds(20)); }
        catch (AbandonedMutexException) { return true; }   // the old copy ended without releasing it: ours now
    }

    /// <summary>The running copy: listen for later starts asking it to come to the front.</summary>
    public static void Listen()
    {
        try
        {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
            ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => ActivateRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) { Log.Write("Single copy: couldn't listen for other starts: " + ex.Message); }
    }

    /// <summary>A second start: let the running copy take the foreground and ask it to come to the front.</summary>
    public static void ActivateRunningCopy()
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);   // a background process may only take the foreground if allowed
            if (EventWaitHandle.TryOpenExisting(ActivateName, out var signal))
                using (signal) signal.Set();
        }
        catch (Exception ex) { Log.Write("Single copy: couldn't reach the running copy: " + ex.Message); }
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
