using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwitchboardRM;

/// <summary>
/// Runs a console program (ssh.exe, a shell ...) inside a pseudo-console (ConPTY), so its screen output arrives as text
/// with terminal escape codes and keystrokes can be sent back. Output and Exited are raised on a background thread.
///
/// Uses Microsoft's conpty.dll from tools\conpty when it's there (the Windows Terminal one, MIT licensed): it can give
/// each terminal's hidden console window an owner, Switchboard's main window, which is what lets Windows sign-in
/// windows (WAM: Connect-MgGraph, Connect-AzAccount ...) appear. Without it, Windows' built-in pseudo-console is used,
/// and those sign-in windows stay invisible.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    private Action<string> _onOutput;
    private Action<int> _onExit;

    private IntPtr _hpc;
    private bool _usesConpty;   // created by conpty.dll: resized and closed by it too
    private IntPtr _process;
    private IntPtr _thread;
    private FileStream _input;
    private FileStream _output;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private int _disposed;

    private PseudoConsole() { }

    /// <param name="onOutput">Screen output, on a background thread.</param>
    /// <param name="onExit">The program's exit code once it ends, on a background thread.</param>
    /// <param name="workingDirectory">Folder to start in (null = the app's current folder).</param>
    /// <param name="environment">Variables to set or replace for the program (null = the app's own environment).</param>
    public static PseudoConsole Start(string commandLine, int cols, int rows, Action<string> onOutput, Action<int> onExit,
        string workingDirectory = null, IDictionary<string, string> environment = null)
    {
        // handlers are in place before anything is read, so the first bytes (e.g. a password prompt) aren't lost
        var pc = new PseudoConsole { _onOutput = onOutput, _onExit = onExit };
        try { pc.StartCore(commandLine, cols, rows, workingDirectory, environment); }
        catch { pc.Dispose(); throw; }
        return pc;
    }

    private void StartCore(string commandLine, int cols, int rows, string workingDirectory, IDictionary<string, string> environment)
    {
        // two pipes: what we write -> the program's keyboard; the program's screen -> what we read
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        int hr = -1;
        if (Conpty.Value is { } conpty)
        {
            hr = conpty.Create(Size(cols, rows), inRead.DangerousGetHandle(), outWrite.DangerousGetHandle(), 0, out _hpc);
            if (hr == 0)
            {
                _usesConpty = true;
                // the hidden console window belongs to Switchboard's window, so WAM sign-in windows appear over it
                if (OwnerWindow != IntPtr.Zero)
                {
                    int r = conpty.Reparent(_hpc, OwnerWindow);
                    if (r != 0) Log.Write($"ConptyReparentPseudoConsole failed (0x{r:X8}); sign-in windows may not appear");
                }
            }
            else Log.Write($"conpty.dll couldn't create a pseudo-console (0x{hr:X8}); using Windows' built-in one");
        }
        if (hr != 0) hr = CreatePseudoConsole(Size(cols, rows), inRead, outWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr, "Couldn't create the pseudo-console (needs Windows 10 1809 or newer).");

        // attach the pseudo-console to the new process
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrs = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrs;
            IntPtr envBlock = environment == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            try
            {
                // started suspended so it joins the job before it can start anything of its own
                uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED |
                             (envBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0);
                string cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : null;
                // CreateProcessW may write into the command-line buffer, so it gets its own copy
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags,
                        envBlock, cwd, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't start: " + commandLine);
                _process = pi.hProcess;
                ProcessJob.Add(pi.hProcess);   // ends with Switchboard, even if it crashes
                ResumeThread(pi.hThread);
                _thread = pi.hThread;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
        }

        // the pseudo-console has its own copies of these ends now
        inRead.Dispose();
        outWrite.Dispose();

        _input = new FileStream(inWrite, FileAccess.Write, 1, false);
        _output = new FileStream(outRead, FileAccess.Read, 1, false);

        new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    private void ReadLoop()
    {
        var bytes = new byte[16 * 1024];
        var chars = new char[16 * 1024 + 4];
        try
        {
            int n;
            while ((n = _output.Read(bytes, 0, bytes.Length)) > 0)
            {
                // the decoder keeps a UTF-8 character that's split across two reads in one piece
                int c = _decoder.GetChars(bytes, 0, n, chars, 0, false);
                if (c > 0) _onOutput?.Invoke(new string(chars, 0, c));
            }
        }
        catch { /* pipe closed */ }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, INFINITE);
        GetExitCodeProcess(_process, out int code);
        // closing the pseudo-console flushes the last output and ends ReadLoop
        var hpc = Interlocked.Exchange(ref _hpc, IntPtr.Zero);
        if (hpc != IntPtr.Zero) Close(hpc);
        _onExit?.Invoke(code);
    }

    /// <summary>Send keystrokes / pasted text (as typed in the terminal).</summary>
    public void Write(string text)
    {
        if (_disposed != 0 || string.IsNullOrEmpty(text)) return;
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            _input.Write(data, 0, data.Length);
            _input.Flush();
        }
        catch { /* the program has gone */ }
    }

    public void Resize(int cols, int rows)
    {
        var hpc = _hpc;
        if (hpc == IntPtr.Zero || cols <= 0 || rows <= 0) return;
        if (_usesConpty && Conpty.Value is { } conpty) conpty.Resize(hpc, Size(cols, rows));
        else ResizePseudoConsole(hpc, Size(cols, rows));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // closing the pseudo-console ends the program attached to it
        var hpc = Interlocked.Exchange(ref _hpc, IntPtr.Zero);
        if (hpc != IntPtr.Zero) Close(hpc);
        if (_process != IntPtr.Zero)
        {
            if (WaitForSingleObject(_process, 0) != 0) TerminateProcess(_process, 1);
        }
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        // the wait thread may still be using the process handle; leave the two handles to process exit
    }

    /// <summary>The app's environment with the given changes, as Windows expects it: "NAME=value\0...\0", sorted.</summary>
    private static string EnvironmentBlock(IDictionary<string, string> changes)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string)e.Value;
        foreach (var kv in changes) vars[kv.Key] = kv.Value;
        var sb = new StringBuilder();
        foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    private static COORD Size(int cols, int rows) =>
        new() { X = (short)Math.Clamp(cols, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

    // ---------------------------------------------------------------- Win32

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_SUSPENDED = 0x00000004;

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr thread);
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr attributes, int size);

    // ---------------------------------------------------------------- Microsoft's conpty.dll (tools\conpty)

    /// <summary>The window that owns every terminal's hidden console window (Switchboard's main window).</summary>
    public static IntPtr OwnerWindow;

    /// <summary>Which pseudo-console new terminals get, for the log and Settings > About.</summary>
    public static string HostDescription => Conpty.Value != null
        ? "Microsoft ConPTY (tools\\conpty): Windows sign-in windows appear"
        : "Windows built-in (no tools\\conpty): Windows sign-in windows can't appear in terminals";

    private void Close(IntPtr hpc)
    {
        if (_usesConpty && Conpty.Value is { } conpty) conpty.Close(hpc);
        else ClosePseudoConsole(hpc);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConptyCreateFn(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConptyResizeFn(IntPtr hPC, COORD size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ConptyCloseFn(IntPtr hPC);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConptyReparentFn(IntPtr hPC, IntPtr newParent);

    private sealed record ConptyApi(ConptyCreateFn Create, ConptyResizeFn Resize, ConptyCloseFn Close, ConptyReparentFn Reparent);

    /// <summary>conpty.dll, loaded once; null = use Windows' built-in pseudo-console.</summary>
    private static readonly Lazy<ConptyApi> Conpty = new(LoadConpty);

    private static ConptyApi LoadConpty()
    {
        string dir = Path.Combine(Tools.Dir, "conpty");
        string dll = Path.Combine(dir, "conpty.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(dir, "OpenConsole.exe")))
        {
            Log.Write("Terminals: Windows' built-in pseudo-console (no tools\\conpty, so WAM sign-in windows won't appear in terminals)");
            return null;
        }
        try
        {
            IntPtr lib = NativeLibrary.Load(dll);
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            var api = new ConptyApi(
                Get<ConptyCreateFn>("ConptyCreatePseudoConsole"),
                Get<ConptyResizeFn>("ConptyResizePseudoConsole"),
                Get<ConptyCloseFn>("ConptyClosePseudoConsole"),
                Get<ConptyReparentFn>("ConptyReparentPseudoConsole"));
            Log.Write("Terminals: Microsoft's conpty.dll from " + dir);
            return api;
        }
        catch (Exception ex)
        {
            Log.Write($"Terminals: couldn't load conpty.dll ({ex.Message}); using Windows' built-in pseudo-console");
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, int exitCode);
}
