using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cameo.Runtime.Stdlib;

/// <summary>A program running on a pseudo-terminal: it sees a terminal, we hold the other end.</summary>
internal sealed class PtyChild
{
    public required int Pid;
    /// <summary>What the program writes (its output and error together, as on a terminal).</summary>
    public required Stream Output;
    /// <summary>What the program reads as its keyboard.</summary>
    public required Stream Input;
    public required Task<int> Exit;
    public required Action Kill;
}

/// <summary>
/// Starts programs on a pseudo-terminal. Unix (Linux, macOS): posix_openpt + posix_spawn, with
/// the terminal opened as the child's standard streams in a new session. Windows: ConPTY.
/// Linux is exercised by the process corpus. Windows has bounded ConPTY output/cancellation
/// checks under redirected hosts; the macOS path remains unqualified.
/// </summary>
internal static class Pty
{
    public static PtyChild Start(string program, IReadOnlyList<string> args, int cols, int rows) =>
        OperatingSystem.IsWindows() ? StartWindows(program, args, cols, rows) : StartUnix(program, args, cols, rows);

    // ------------------------------------------------------------------ Unix

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, UIntPtr request, ref WinSize size);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
    [DllImport("libc")] private static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport("libc")]
    private static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr actions, IntPtr attr,
        IntPtr[] argv, IntPtr[] envp);

    // A null-terminated array of UTF-8 strings, as C's argv and envp are.
    private static IntPtr[] CStrings(IEnumerable<string> items) =>
        items.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();

    private static void Free(IntPtr[] strings)
    {
        foreach (IntPtr p in strings) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
    }

    private static readonly object PtsNameLock = new(); // ptsname returns a shared buffer

    private static PtyChild StartUnix(string program, IReadOnlyList<string> args, int cols, int rows)
    {
        bool mac = OperatingSystem.IsMacOS();
        const int O_RDWR = 2;
        int O_NOCTTY = mac ? 0x20000 : 0x100, O_CLOEXEC = mac ? 0x1000000 : 0x80000;
        short POSIX_SPAWN_SETSID = (short)(mac ? 0x0400 : 0x80);
        ulong TIOCSWINSZ = mac ? 0x80087467UL : 0x5414UL;

        int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (master < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "cannot open a pseudo-terminal");
        IntPtr actions = Marshal.AllocHGlobal(1024), attr = Marshal.AllocHGlobal(1024); // opaque libc structures, generously sized
        bool actionsReady = false, attrReady = false;
        try
        {
            if (grantpt(master) != 0 || unlockpt(master) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "cannot set up the pseudo-terminal");
            string slave;
            lock (PtsNameLock)
            {
                IntPtr name = ptsname(master);
                if (name == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "cannot name the pseudo-terminal");
                slave = Marshal.PtrToStringUTF8(name)!;
            }
            var size = new WinSize { Cols = (ushort)cols, Rows = (ushort)rows };
            ioctl(master, (UIntPtr)TIOCSWINSZ, ref size); // best effort: a size of 0x0 only confuses full-screen programs

            if (posix_spawn_file_actions_init(actions) != 0) throw new InvalidOperationException("posix_spawn_file_actions_init failed");
            actionsReady = true;
            // In a new session, opening the terminal as standard input makes it the controlling terminal.
            posix_spawn_file_actions_addopen(actions, 0, slave, O_RDWR, 0);
            posix_spawn_file_actions_adddup2(actions, 0, 1);
            posix_spawn_file_actions_adddup2(actions, 0, 2);
            if (posix_spawnattr_init(attr) != 0) throw new InvalidOperationException("posix_spawnattr_init failed");
            attrReady = true;
            posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID);

            var env = new List<string>();
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env.Add($"{e.Key}={e.Value}");
            if (Environment.GetEnvironmentVariable("TERM") == null) env.Add("TERM=xterm");
            IntPtr[] argv = CStrings(new[] { program }.Concat(args)), envp = CStrings(env);
            int rc, pid;
            try
            {
                rc = posix_spawnp(out pid, program, actions, attr, argv, envp);
            }
            finally
            {
                Free(argv);
                Free(envp);
            }
            if (rc != 0) throw new Win32Exception(rc);

            int writer = dup(master);
            var output = new FileStream(new SafeFileHandle((IntPtr)master, ownsHandle: true), FileAccess.Read, 1, isAsync: false);
            var input = new FileStream(new SafeFileHandle((IntPtr)writer, ownsHandle: true), FileAccess.Write, 1, isAsync: false);
            master = -1; // owned by the stream now
            var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                int status;
                while (waitpid(pid, out status, 0) < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */) { }
                // exited normally: the code; killed by a signal: 128 + signal, as shells report it
                exit.SetResult((status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f));
            }) { IsBackground = true, Name = "tlvm pty wait" }.Start();
            return new PtyChild
            {
                Pid = pid, Output = output, Input = input, Exit = exit.Task,
                Kill = () => { if (!exit.Task.IsCompleted) kill(-pid, 9); }, // the whole session it leads
            };
        }
        finally
        {
            if (actionsReady) posix_spawn_file_actions_destroy(actions);
            if (attrReady) posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attr);
            if (master >= 0) close(master);
        }
    }

    // ------------------------------------------------------------------ Windows (ConPTY)

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    private static PtyChild StartWindows(string program, IReadOnlyList<string> args, int cols, int rows)
    {
        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
        FileStream? input = null, output = null;
        IntPtr console = IntPtr.Zero, list = IntPtr.Zero;
        ProcessInformation info = default;
        bool attributesInitialized = false, transferred = false;
        try
        {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            int hr = CreatePseudoConsole(new Coord { X = (short)cols, Y = (short)rows }, inRead, outWrite, 0, out IntPtr createdConsole);
            if (hr != 0) throw new Win32Exception(hr, "cannot create a pseudo-console");
            console = createdConsole;
            // ConPTY duplicated these two ends. Keep the caller ends locally owned until
            // every managed wrapper and the exit waiter have been constructed successfully.
            inRead.Dispose();
            outWrite.Dispose();

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            attributesInitialized = true;
            const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var startup = new StartupInfoEx { lpAttributeList = list };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // Explicit null standard handles let ConPTY supply its console handles.
            // Without this flag, a redirected parent's logical stdout/stderr can be
            // inherited even with inheritHandles=false, bypassing the pseudo-console.
            const int STARTF_USESTDHANDLES = 0x00000100;
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            var commandLine = new StringBuilder();
            foreach (string a in new[] { program }.Concat(args))
            {
                if (commandLine.Length > 0) commandLine.Append(' ');
                AppendWindowsArgument(commandLine, a);
            }
            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref startup, out ProcessInformation createdProcess))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            info = createdProcess;
            CloseHandle(info.hThread);
            info.hThread = IntPtr.Zero;

            output = new FileStream(outRead, FileAccess.Read, 1, isAsync: false);
            input = new FileStream(inWrite, FileAccess.Write, 1, isAsync: false);
            var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            IntPtr childProcess = info.hProcess, childConsole = console;
            var processLifetime = new object();
            bool processClosed = false;
            var child = new PtyChild
            {
                Pid = info.dwProcessId,
                Output = output,
                Input = input,
                Exit = exit.Task,
                Kill = () =>
                {
                    // Synchronize with handle closure: a reused native handle must never
                    // become a target between CloseHandle and completing the exit task.
                    lock (processLifetime)
                        if (!processClosed) TerminateProcess(childProcess, 1);
                },
            };
            var waiter = new Thread(() =>
            {
                Exception? failure = null;
                uint code = 0;
                try
                {
                    if (WaitForSingleObject(childProcess, 0xFFFFFFFF) != 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    if (!GetExitCodeProcess(childProcess, out code))
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { ClosePseudoConsole(childConsole); } // ends the output stream
                    catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
                    finally
                    {
                        lock (processLifetime)
                        {
                            processClosed = true;
                            CloseHandle(childProcess);
                        }
                    }
                }
                if (failure == null) exit.TrySetResult(unchecked((int)code));
                else exit.TrySetException(failure);
            }) { IsBackground = true, Name = "tlvm pty wait" };
            waiter.Start();
            // No fallible construction remains: the child owns the streams and the
            // running waiter owns the console/process handles from this point.
            transferred = true;
            return child;
        }
        finally
        {
            if (attributesInitialized) DeleteProcThreadAttributeList(list);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            inRead?.Dispose();
            outWrite?.Dispose();
            if (!transferred)
            {
                // A child created before a later construction failure still belongs to
                // this frame. Stop it before disconnecting its pipes and console.
                if (info.hProcess != IntPtr.Zero)
                {
                    TerminateProcess(info.hProcess, 1);
                    WaitForSingleObject(info.hProcess, 5000);
                }
                try { output?.Dispose(); }
                finally
                {
                    try { input?.Dispose(); }
                    finally
                    {
                        // SafeHandle references remain here even after wrapping, so each
                        // native pipe is released even if a stream constructor/disposal fails.
                        inWrite?.Dispose();
                        outRead?.Dispose();
                        try { if (console != IntPtr.Zero) ClosePseudoConsole(console); }
                        finally
                        {
                            if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread);
                            if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);
                        }
                    }
                }
            }
        }
    }
    // The quoting rules of the Microsoft C runtime (what most Windows programs parse their command line with).
    private static void AppendWindowsArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            int slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) sb.Append('\\', slashes * 2);
            else if (arg[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(arg[i]);
        }
        sb.Append('"');
    }
}
