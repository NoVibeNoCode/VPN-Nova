using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nova.Service;

/// The few Win32 calls .NET doesn't wrap. Every method is a no-op off Windows (console test mode on macOS).
static partial class Native
{
    // MARK: Console: graceful stop of sing-box with Ctrl+Break

    const uint CtrlBreakEvent = 1;
    delegate bool ConsoleCtrlHandler(uint ctrlType);
    static ConsoleCtrlHandler? _handler;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    /// A service has no console. sing-box (a console program) is started on this hidden one, so a
    /// Ctrl+Break sent to the console reaches it; the service itself swallows the event.
    public static void PrepareConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (GetConsoleWindow() == IntPtr.Zero && AllocConsole())
        {
            var w = GetConsoleWindow();
            if (w != IntPtr.Zero) ShowWindow(w, 0);
        }
        _handler = type => type == CtrlBreakEvent;
        SetConsoleCtrlHandler(_handler, true);
    }

    /// Ctrl+Break to every process on our console: sing-box shuts down cleanly (routes, Wintun adapter).
    public static void SendCtrlBreak()
    {
        if (OperatingSystem.IsWindows()) GenerateConsoleCtrlEvent(CtrlBreakEvent, 0);
    }

    // MARK: Job object: sing-box never outlives the service

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
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
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    static IntPtr _job;

    public static void AddToKillJob(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_job == IntPtr.Zero)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
            _job = job;
        }
        AssignProcessToJobObject(_job, p.Handle);
    }

    // MARK: Final path of an open file (defeats a file swapped for a link between check and read)

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint size, uint flags);

    public static string? FinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buf = new char[1024];
        var n = GetFinalPathNameByHandle(handle, buf, (uint)buf.Length, 0);
        if (n == 0 || n >= buf.Length) return null;
        var s = new string(buf, 0, (int)n);
        // \\?\C:\x → C:\x ; \\?\UNC\server\share → \\server\share
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }
}

static class SafeCopy
{
    /// Copies a rule set the guard already approved. The source is opened once; on Windows the handle's
    /// real path must still be inside the owner's folder, so a last-moment swap for a link is refused.
    public static void CopyRegularFile(string src, string dst, string allowedDir)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (OperatingSystem.IsWindows())
        {
            var real = Native.FinalPath(input.SafeFileHandle);
            var dir = Path.GetFullPath(allowedDir).TrimEnd('\\');
            if (real == null || !string.Equals(Path.GetDirectoryName(real), dir, StringComparison.OrdinalIgnoreCase))
                throw new Nova.Core.CoreException($"набор правил подменён: {Path.GetFileName(src)}");
        }
        if (input.Length > 64 * 1024 * 1024) throw new Nova.Core.CoreException($"набор правил слишком большой: {Path.GetFileName(src)}");
        var tmp = dst + ".tmp";
        using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        File.Move(tmp, dst, overwrite: true);
    }
}
