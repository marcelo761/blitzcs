namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Which return register(s), besides EAX and EDX, a call captures. Chosen explicitly at the call site; nothing is guessed from the export.</summary>
public enum GuestReturn
{
    /// <summary>EAX (and EDX for 64-bit results). The x87 stack must be balanced when the guest returns.</summary>
    Eax = 0,
    /// <summary>ST0: the x87 return of float, double and long double in cdecl/stdcall (MSVC and GCC). Popped by the runtime.</summary>
    St0 = 1,
    /// <summary>XMM0 (low 128 bits, raw): for routines compiled with an SSE return convention (e.g. __vectorcall, Clang/GCC -mfpmath=sse regparm builds). Opt-in only.</summary>
    Xmm0 = 2,
}

/// <summary>
/// Return registers of one guest call. EAX and EDX are always valid; the ST0 members only when the call was made with
/// <see cref="GuestReturn.St0"/> and the XMM0 members only with <see cref="GuestReturn.Xmm0"/>: reading the others throws instead of returning stale zeros.
/// </summary>
public readonly struct GuestCallResult
{
    private readonly ulong st0Mantissa, xmm0Low, xmm0High;
    private readonly ushort st0SignExponent;
    private readonly double st0Double;
    private readonly float st0Single;

    internal GuestCallResult(uint eax, uint edx, GuestReturn kind, ulong st0Mantissa, ushort st0SignExponent, double st0Double, float st0Single, ulong xmm0Low, ulong xmm0High)
    {
        Eax = eax; Edx = edx; Kind = kind;
        this.st0Mantissa = st0Mantissa; this.st0SignExponent = st0SignExponent; this.st0Double = st0Double; this.st0Single = st0Single;
        this.xmm0Low = xmm0Low; this.xmm0High = xmm0High;
    }

    public GuestReturn Kind { get; }
    public uint Eax { get; }
    public uint Edx { get; }

    /// <summary>EDX:EAX, the return convention of 64-bit integers (and of 5..8 byte structs for MSVC).</summary>
    public ulong Int64 => ((ulong)Edx << 32) | Eax;

    private void Require(GuestReturn kind)
    {
        if (Kind != kind) throw new InvalidOperationException($"This result was captured with GuestReturn.{Kind}; the {kind} registers were not captured.");
    }

    /// <summary>ST0 rounded to double with the x87 rounding mode in effect (round to nearest even by default). NaN payloads, infinities and signed zero are preserved.</summary>
    public double St0Double { get { Require(GuestReturn.St0); return st0Double; } }

    /// <summary>ST0 narrowed to single precision directly from the 80-bit value (one rounding, to nearest even): this is what a float-returning callee's caller observes.</summary>
    public float St0Single { get { Require(GuestReturn.St0); return st0Single; } }

    /// <summary>The 64-bit significand of ST0 in 80-bit extended format (explicit integer bit).</summary>
    public ulong St0Mantissa { get { Require(GuestReturn.St0); return st0Mantissa; } }

    /// <summary>Sign (bit 15) and biased exponent of ST0 in 80-bit extended format.</summary>
    public ushort St0SignExponent { get { Require(GuestReturn.St0); return st0SignExponent; } }

    public ulong Xmm0Low { get { Require(GuestReturn.Xmm0); return xmm0Low; } }
    public ulong Xmm0High { get { Require(GuestReturn.Xmm0); return xmm0High; } }
    public float Xmm0Single { get { Require(GuestReturn.Xmm0); return BitConverter.UInt32BitsToSingle((uint)xmm0Low); } }
    public double Xmm0Double { get { Require(GuestReturn.Xmm0); return BitConverter.UInt64BitsToDouble(xmm0Low); } }
}

/// <summary>A bound guest export. Calls are allocation-free and coordinated with module disposal.</summary>
public sealed class GuestFunction
{
    private readonly GuestModule module;
    private readonly nint hostTarget;

    internal GuestFunction(GuestModule module, string name, nint hostTarget, GuestCallingConvention convention, int argCount)
    {
        this.module = module;
        this.hostTarget = hostTarget;
        Name = name;
        Convention = convention;
        ArgCount = argCount;
    }

    public string Name { get; }
    public GuestCallingConvention Convention { get; }

    /// <summary>Number of 32-bit stack words the export takes, including a hidden struct-return pointer when one is used.</summary>
    public int ArgCount { get; }

    /// <summary>Calls the export with exactly <see cref="ArgCount"/> 32-bit stack arguments and returns EAX. A guest that returns a value in ST0 makes this call fail with <see cref="GuestAbiException"/> (use <see cref="CallDouble"/>/<see cref="CallSingle"/>).</summary>
    public uint Call(params ReadOnlySpan<uint> args)
    {
        CheckArgs(args.Length);
        return module.Invoke(hostTarget, args);
    }

    /// <summary>Calls the export and captures the return registers selected by <paramref name="kind"/>.</summary>
    public GuestCallResult Invoke(GuestReturn kind, params ReadOnlySpan<uint> args)
    {
        CheckArgs(args.Length);
        return module.Invoke(hostTarget, args, kind);
    }

    /// <summary>64-bit integer return in EDX:EAX.</summary>
    public ulong CallUInt64(params ReadOnlySpan<uint> args) => Invoke(GuestReturn.Eax, args).Int64;

    /// <summary>float/double/long double return in ST0, rounded to double.</summary>
    public double CallDouble(params ReadOnlySpan<uint> args) => Invoke(GuestReturn.St0, args).St0Double;

    /// <summary>float return in ST0 (narrowed once, to nearest even, from the 80-bit register).</summary>
    public float CallSingle(params ReadOnlySpan<uint> args) => Invoke(GuestReturn.St0, args).St0Single;

    /// <summary>Opt-in SSE return: XMM0 low 32 bits as float.</summary>
    public float CallXmm0Single(params ReadOnlySpan<uint> args) => Invoke(GuestReturn.Xmm0, args).Xmm0Single;

    /// <summary>Opt-in SSE return: XMM0 low 64 bits as double.</summary>
    public double CallXmm0Double(params ReadOnlySpan<uint> args) => Invoke(GuestReturn.Xmm0, args).Xmm0Double;

    /// <summary>
    /// Struct of 1, 2, 4 or 8 bytes returned in registers (MSVC rule; GCC with -freg-struct-return): the first <paramref name="size"/> bytes of EAX:EDX, little-endian.
    /// </summary>
    public byte[] CallStructInRegisters(int size, params ReadOnlySpan<uint> args)
    {
        if (size is not (1 or 2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(size), size, "Only structs of 1, 2, 4 or 8 bytes are returned in registers.");
        var r = Invoke(GuestReturn.Eax, args);
        var bytes = new byte[size];
        var value = r.Int64;
        for (var i = 0; i < size; i++) bytes[i] = (byte)(value >> (8 * i));
        return bytes;
    }

    /// <summary>
    /// Struct returned through a hidden first stack argument (any size; the Windows rule for structs that are not 1, 2, 4 or 8 bytes). The runtime allocates the
    /// result buffer, passes its address as the first argument (<see cref="ArgCount"/> includes it), and verifies that the callee returned it in EAX.
    /// The callee pops the pointer (stdcall) or the caller does (cdecl); either way the call frame is discarded, so both are handled.
    /// </summary>
    public byte[] CallStructViaHiddenPointer(int size, params ReadOnlySpan<uint> args)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (args.Length + 1 != ArgCount)
            throw new ArgumentException($"Export '{Name}' was bound with {ArgCount} stack word(s) including the hidden struct pointer, but {args.Length} explicit argument(s) were supplied.", nameof(args));
        using var buffer = module.Memory.Allocate(Math.Max(1, size));
        Span<uint> all = args.Length < 15 ? stackalloc uint[args.Length + 1] : new uint[args.Length + 1];
        all[0] = buffer.Address;
        args.CopyTo(all[1..]);
        var result = module.Invoke(hostTarget, all, GuestReturn.Eax);
        if (result.Eax != buffer.Address)
            throw new GuestAbiException($"Export '{Name}' did not return the hidden struct pointer in EAX (expected 0x{buffer.Address:X8}, got 0x{result.Eax:X8}); it does not follow the struct-return convention.");
        var bytes = new byte[size];
        if (size > 0) buffer.Read(0, bytes);
        return bytes;
    }

    private void CheckArgs(int count)
    {
        if (count != ArgCount)
            throw new ArgumentException($"Export '{Name}' was bound with {ArgCount} argument(s) but {count} were supplied.", "args");
    }
}
