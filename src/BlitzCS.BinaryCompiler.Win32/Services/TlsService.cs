namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Per-thread slot values keyed by slot generation, so TlsFree invalidates every thread's value lazily.</summary>
internal sealed class TlsState
{
    public const int Slots = 64; // TLS_MINIMUM_AVAILABLE
    private readonly object gate = new();
    private readonly int[] generation = new int[Slots];
    private readonly bool[] allocated = new bool[Slots];
    private int nextGeneration;
    private readonly ThreadLocal<(int Generation, uint Value)[]> values = new(() => new (int, uint)[Slots]);

    public int Alloc()
    {
        lock (gate)
            for (var i = 0; i < Slots; i++)
                if (!allocated[i]) { allocated[i] = true; generation[i] = ++nextGeneration; return i; }
        return -1;
    }

    public bool Free(uint index) { lock (gate) { if (index >= Slots || !allocated[index]) return false; allocated[index] = false; return true; } }

    public bool TryGet(uint index, out uint value)
    {
        value = 0;
        int g;
        lock (gate) { if (index >= Slots || !allocated[index]) return false; g = generation[index]; }
        var entry = values.Value![index];
        value = entry.Generation == g ? entry.Value : 0;
        return true;
    }

    public bool TrySet(uint index, uint value)
    {
        int g;
        lock (gate) { if (index >= Slots || !allocated[index]) return false; g = generation[index]; }
        values.Value![index] = (g, value);
        return true;
    }
}

/// <summary>
/// KERNEL32 thread-local storage (Microsoft Learn, "Thread Local Storage"): TlsAlloc returns TLS_OUT_OF_INDEXES (0xFFFFFFFF) when exhausted;
/// a new slot reads as 0 on every thread; TlsGetValue sets the last error to ERROR_SUCCESS on success (so 0 can be told from failure);
/// invalid or freed indexes fail with ERROR_INVALID_PARAMETER; TlsFree makes the slot's values vanish on every thread.
/// Storage is a host-side per-thread table of 64 slots (the minimum Windows guarantees). Planned unification: the TEB contract places the same
/// 64 slots at TEB+0xE10; once guest code can read FS:[0xE10+4*i] this table must become the TEB array itself so that inlined TLS access and
/// these APIs observe one storage. TLS_OUT_OF_INDEXES sets ERROR_NO_MORE_ITEMS (approximation: Microsoft does not document the error code).
/// </summary>
public sealed class TlsService : Win32Service
{
    public override string Name => "Kernel32.Tls";

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "TlsAlloc", 0, c => { var i = c.Ctx.Tls.Alloc(); return i < 0 ? c.Ctx.Fail(Win32Error.NoMoreItems, 0xFFFFFFFF) : (uint)i; });
        Std(K, "TlsFree", 1, c => c.Ctx.Tls.Free(c[0]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
        Std(K, "TlsGetValue", 1, c =>
        {
            if (!c.Ctx.Tls.TryGet(c[0], out var v)) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Ctx.LastError = Win32Error.Success;
            return v;
        });
        Std(K, "TlsSetValue", 2, c => c.Ctx.Tls.TrySet(c[0], c[1]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
    }
}
