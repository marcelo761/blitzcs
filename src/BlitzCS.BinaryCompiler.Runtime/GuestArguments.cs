using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// One argument word of a call made with <see cref="GuestFunction.InvokeWithBuffers"/>: a plain 32-bit value, or the guest address of a
/// runtime-owned checked buffer that is input, output or both. Buffers live only for the duration of the call; the guest never receives a host pointer.
/// Buffers are page-granular guest allocations: a callee that writes past <c>size</c> is outside what the runtime can detect.
/// </summary>
public readonly struct GuestArg
{
    private enum Mode : byte { Word, In, Out, InOut }
    private readonly Mode mode;
    private GuestArg(uint word, Mode mode, int size, byte[]? input) { Word = word; this.mode = mode; Size = size; Input = input; }

    internal uint Word { get; }
    internal int Size { get; }
    internal byte[]? Input { get; }
    internal bool IsBuffer => mode != Mode.Word;
    internal bool IsOutput => mode is Mode.Out or Mode.InOut;

    public static implicit operator GuestArg(uint value) => new(value, Mode.Word, 0, null);
    public static implicit operator GuestArg(int value) => new((uint)value, Mode.Word, 0, null);
    public static GuestArg Single(float value) => new(GuestValue.FromSingle(value), Mode.Word, 0, null);

    /// <summary>A zero-filled buffer of <paramref name="size"/> bytes the callee writes to; read it back with <see cref="GuestCallOutcome"/>.</summary>
    public static GuestArg Out(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return new(0, Mode.Out, size, null);
    }

    /// <summary>An output buffer sized for one <typeparamref name="T"/> (int, float, long, double, a blittable struct).</summary>
    public static unsafe GuestArg Out<T>() where T : unmanaged => Out(sizeof(T));

    /// <summary>An output buffer for <paramref name="count"/> elements of <typeparamref name="T"/>.</summary>
    public static unsafe GuestArg OutArray<T>(int count) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Out(checked(count * sizeof(T)));
    }

    /// <summary>A read-only copy of <paramref name="data"/> passed by address (the callee may still scribble on its private copy).</summary>
    public static GuestArg In(ReadOnlySpan<byte> data) => new(0, Mode.In, data.Length, data.ToArray());

    /// <summary>A copy of <paramref name="value"/> passed by address.</summary>
    public static GuestArg In<T>(T value) where T : unmanaged => In(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>A buffer that starts with <paramref name="data"/> and is copied back after the call.</summary>
    public static GuestArg InOut(ReadOnlySpan<byte> data) => new(0, Mode.InOut, data.Length, data.ToArray());

    public static GuestArg InOut<T>(T value) where T : unmanaged => InOut(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));
}

/// <summary>Registers and copied-back output buffers of one <see cref="GuestFunction.InvokeWithBuffers"/> call. Outputs are indexed by argument position.</summary>
public sealed class GuestCallOutcome
{
    private readonly byte[]?[] outputs;
    internal GuestCallOutcome(GuestCallResult registers, byte[]?[] outputs) { Registers = registers; this.outputs = outputs; }

    public GuestCallResult Registers { get; }
    public uint Eax => Registers.Eax;

    /// <summary>The bytes the callee left in the Out/InOut buffer passed as argument <paramref name="argIndex"/> (a copy).</summary>
    public byte[] Bytes(int argIndex)
    {
        if ((uint)argIndex >= (uint)outputs.Length || outputs[argIndex] is not { } bytes)
            throw new ArgumentException($"Argument {argIndex} is not an output buffer of this call.", nameof(argIndex));
        return (byte[])bytes.Clone();
    }

    /// <summary>The output buffer reinterpreted as one <typeparamref name="T"/>; the buffer must be exactly that size.</summary>
    public unsafe T Get<T>(int argIndex) where T : unmanaged
    {
        var bytes = Bytes(argIndex);
        if (bytes.Length != sizeof(T)) throw new ArgumentException($"Output {argIndex} is {bytes.Length} byte(s); {typeof(T).Name} needs {sizeof(T)}.", nameof(argIndex));
        return MemoryMarshal.Read<T>(bytes);
    }

    /// <summary>The output buffer as an array of <typeparamref name="T"/> (its length must be a multiple of the element size).</summary>
    public unsafe T[] GetArray<T>(int argIndex) where T : unmanaged
    {
        var bytes = Bytes(argIndex);
        if (bytes.Length % sizeof(T) != 0) throw new ArgumentException($"Output {argIndex} is {bytes.Length} byte(s), not a whole number of {typeof(T).Name}.", nameof(argIndex));
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    public int GetInt32(int argIndex) => Get<int>(argIndex);
    public uint GetUInt32(int argIndex) => Get<uint>(argIndex);
    public long GetInt64(int argIndex) => Get<long>(argIndex);
    public float GetSingle(int argIndex) => Get<float>(argIndex);
    public double GetDouble(int argIndex) => Get<double>(argIndex);
}
