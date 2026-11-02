using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwitchboardRM;

/// <summary>
/// Runs a console program (here ssh.exe) inside a Windows pseudo-console (ConPTY, Windows 10 1809+), so its
/// screen output arrives as text with terminal escape codes and keystrokes can be sent back. The same mechanism
/// Windows Terminal uses. Output and Exited are raised on a background thread.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    private Action<string> _onOutput;
    private Action<int> _onExit;

    private IntPtr _hpc;
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

        int hr = CreatePseudoConsole(Size(cols, rows), inRead, outWrite, 0, out _hpc);
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
                uint flags = EXTENDED_STARTUPINFO_PRESENT | (envBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0);
                string cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : null;
                // CreateProcessW may write into the command-line buffer, so it gets its own copy
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags,
                        envBlock, cwd, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't start: " + commandLine);
                _process = pi.hProcess;
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
        if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
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
        if (hpc != IntPtr.Zero && cols > 0 && rows > 0) ResizePseudoConsole(hpc, Size(cols, rows));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // closing the pseudo-console ends the program attached to it
        var hpc = Interlocked.Exchange(ref _hpc, IntPtr.Zero);
        if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
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
