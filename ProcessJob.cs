using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwitchboardRM;

/// <summary>
/// A Windows job object that every helper process Switchboard starts goes into (tunnels, ssh, shells, sign-in).
/// It's set to end all of them when the job closes, which Windows does whenever Switchboard ends: a normal close,
/// a crash or being killed from Task Manager. So no tunnel or shell can outlive the app.
/// </summary>
internal static class ProcessJob
{
    private static readonly IntPtr _job = Create();

    private static IntPtr Create()
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                    Log.Write("Couldn't set up the job object: error " + Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return job;   // never closed: Windows closes it when Switchboard ends, which ends the processes in it
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't create the job object: " + ex.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>Put a started process (and anything it starts afterwards) in the job.</summary>
    public static void Add(IntPtr processHandle)
    {
        if (_job == IntPtr.Zero || processHandle == IntPtr.Zero) return;
        if (!AssignProcessToJobObject(_job, processHandle))
            Log.Write("Couldn't tie a helper process to Switchboard: error " + Marshal.GetLastWin32Error());
    }

    public static void Add(Process p)
    {
        try { Add(p.Handle); } catch { /* it has already exited */ }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

/// <summary>
/// Notices when the previous run didn't close cleanly (crash, killed, power loss): a marker file exists while
/// Switchboard runs and is removed on a clean exit.
/// </summary>
internal static class RunMarker
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "running.marker");

    /// <summary>Note that Switchboard is running. True if the last run ended without closing cleanly.</summary>
    public static bool Begin()
    {
        bool crashed = false;
        try
        {
            if (File.Exists(FilePath))
            {
                // still running in another copy of Switchboard? then it isn't a crash
                crashed = !(int.TryParse(File.ReadAllText(FilePath).Trim(), out int pid) && IsSwitchboard(pid));
            }
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, Environment.ProcessId.ToString());
        }
        catch (Exception ex) { Log.Write("Couldn't write the run marker: " + ex.Message); }
        return crashed;
    }

    /// <summary>A clean exit: remove the marker (if it's this run's).</summary>
    public static void End()
    {
        try
        {
            if (File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() == Environment.ProcessId.ToString())
                File.Delete(FilePath);
        }
        catch { }
    }

    private static bool IsSwitchboard(int pid)
    {
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            using var me = Process.GetCurrentProcess();
            return p.ProcessName.Equals(me.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
