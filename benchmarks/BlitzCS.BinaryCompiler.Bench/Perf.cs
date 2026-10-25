using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Bench;

/// <summary>Thin perf_event_open wrapper: user-space-only hardware counters of the calling thread.</summary>
internal sealed unsafe class PerfCounters : IDisposable
{
    public static readonly string[] Names = ["cycles", "instructions", "branch_misses", "cache_misses"];
    private static readonly ulong[] Configs = [0, 1, 5, 3]; // PERF_COUNT_HW_CPU_CYCLES, INSTRUCTIONS, BRANCH_MISSES, CACHE_MISSES
    private readonly int[] fds = new int[4];

    [DllImport("libc", SetLastError = true)] private static extern long syscall(long number, void* attr, int pid, int cpu, int groupFd, ulong flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, ulong arg);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, void* buf, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    public PerfCounters()
    {
        var attr = stackalloc byte[128];
        for (var i = 0; i < 4; i++)
        {
            new Span<byte>(attr, 128).Clear();
            *(uint*)attr = 0;                      // PERF_TYPE_HARDWARE
            *(uint*)(attr + 4) = 128;              // size
            *(ulong*)(attr + 8) = Configs[i];
            *(ulong*)(attr + 40) = 1UL | (1UL << 5) | (1UL << 6); // disabled | exclude_kernel | exclude_hv
            fds[i] = (int)syscall(298, attr, 0, -1, -1, 0);   // SYS_perf_event_open (x86-64), this thread
        }
    }

    public bool Available(int i) => fds[i] >= 0;

    public void Start()
    {
        foreach (var fd in fds) if (fd >= 0) { ioctl(fd, 0x2403, 0); ioctl(fd, 0x2400, 0); }
    }

    /// <summary>Stops counting and returns the four values (-1 for an unavailable counter).</summary>
    public long[] Stop()
    {
        var r = new long[4];
        foreach (var fd in fds) if (fd >= 0) ioctl(fd, 0x2401, 0);
        for (var i = 0; i < 4; i++)
        {
            if (fds[i] < 0) { r[i] = -1; continue; }
            long v; r[i] = read(fds[i], &v, 8) == 8 ? v : -1;
        }
        return r;
    }

    public void Dispose() { foreach (var fd in fds) if (fd >= 0) close(fd); }
}

internal static class Affinity
{
    [DllImport("libc", SetLastError = true)] private static extern unsafe int sched_setaffinity(int pid, nuint size, ulong* mask);
    public static bool Pin(int cpu)
    {
        unsafe { var m = stackalloc ulong[16]; for (var i = 0; i < 16; i++) m[i] = 0; m[cpu / 64] = 1UL << (cpu % 64); return sched_setaffinity(0, 128, m) == 0; }
    }
}
