using System.Runtime.InteropServices;

static class WindowsCounters
{
    private const uint QueryLimitedInformation = 0x1000;

    public static CpuTimes? ReadCpu()
    {
        return GetSystemTimes(out var idle, out var kernel, out var user)
            ? new CpuTimes(idle.Ticks, kernel.Ticks + user.Ticks)
            : null;
    }

    public static ProcessTimes? ReadProcess(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            return GetProcessTimes(handle, out var created, out _, out var kernel, out var user)
                ? new ProcessTimes(created.Ticks, kernel.Ticks + user.Ticks)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static MemoryEntry? ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? new MemoryEntry(status.TotalPhysical - status.AvailablePhysical, status.TotalPhysical)
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Ticks => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memoryStatus);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime created, out FileTime exited,
        out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

readonly record struct CpuTimes(ulong Idle, ulong Total);
readonly record struct ProcessTimes(ulong Created, ulong Total);
