using System.Buffers.Binary;

namespace BlitzCS.NativeInterop;

/// <summary>Native field kinds addressed by a BASIC legacy field image.</summary>
/// <remarks>Each kind consumes one 4-byte BASIC slot except <see cref="UInt64"/>, which consumes two
/// (low, then high). Pointer-sized fields are zero-extended on input; on output a value wider than
/// 32 bits is either saturated to <c>0xFFFFFFFF</c> or rejected with <see cref="OverflowException"/>.</remarks>
public enum NativeStructField { Int32, UInt32, Float32, UInt64, UIntPtrSaturating, UIntPtrChecked }

public enum NativeStructDirection { In, Out, InOut }

/// <summary>Translates a BASIC legacy field image to a sequential, naturally aligned native struct
/// for one synchronous call, and copies it back according to the declared direction.</summary>
/// <remarks>Natural alignment matches Win32/Win64 (MSVC default packing) and x86-64 System V. It does not
/// describe the i386 System V ABI, where 8-byte fields are 4-byte aligned. The native copy is pinned only
/// while the callback runs; the callback must not retain the address.</remarks>
public sealed class NativeStructLayout
{
    private readonly NativeStructField[] fields;
    private readonly int[] offsets;

    public int PointerSize { get; }
    public int Size { get; }
    public int Alignment { get; }
    /// <summary>Minimum BASIC field-image length in bytes.</summary>
    public int ImageLength { get; }
    public IReadOnlyList<int> Offsets => offsets;

    private NativeStructLayout(int pointerSize, NativeStructField[] fields)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize), "Pointer size must be 4 or 8.");
        if (fields.Length == 0) throw new ArgumentException("A native struct requires at least one field.", nameof(fields));
        PointerSize = pointerSize;
        this.fields = fields;
        offsets = new int[fields.Length];
        int offset = 0, alignment = 1, slots = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            if (!Enum.IsDefined(fields[i])) throw new ArgumentOutOfRangeException(nameof(fields));
            var size = NativeSize(fields[i]);
            offset = (offset + size - 1) / size * size;
            offsets[i] = offset;
            offset += size;
            alignment = Math.Max(alignment, size);
            slots += fields[i] == NativeStructField.UInt64 ? 2 : 1;
        }
        Alignment = alignment;
        Size = (offset + alignment - 1) / alignment * alignment;
        ImageLength = slots * 4;
    }

    /// <summary>A layout for the current process.</summary>
    public static NativeStructLayout Sequential(params NativeStructField[] fields) => new(IntPtr.Size, [.. fields]);

    /// <summary>A layout for an explicit target pointer size; only a current-process layout can be invoked.</summary>
    public static NativeStructLayout Sequential(int pointerSize, params NativeStructField[] fields) => new(pointerSize, [.. fields]);

    /// <summary>Calls <paramref name="call"/> with a native copy of a read-only image (NULL when empty).</summary>
    public TResult Invoke<TResult>(ReadOnlySpan<byte> image, Func<nint, TResult> call)
    {
        if (image.IsEmpty) return Call(call);
        var native = Encode(image);
        return Pin(native, call);
    }

    /// <summary>Calls <paramref name="call"/> with a native copy of <paramref name="image"/> (NULL when empty)
    /// and copies the result back unless the direction is <see cref="NativeStructDirection.In"/>.</summary>
    public TResult Invoke<TResult>(Span<byte> image, NativeStructDirection direction, Func<nint, TResult> call)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (image.IsEmpty) return Call(call);
        var native = direction == NativeStructDirection.Out ? Validate(image, new byte[Size]) : Encode(image);
        var result = Pin(native, call);
        if (direction != NativeStructDirection.In) Decode(native, image);
        return result;
    }

    private TResult Call<TResult>(Func<nint, TResult> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        RequireCurrentProcess();
        return call(0);
    }

    private TResult Pin<TResult>(byte[] native, Func<nint, TResult> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var buffer = new NativeBuffer(native.Length);
        buffer.WithBytes(0, native.Length, bytes => { native.CopyTo(bytes); return 0; });
        var result = buffer.WithPointer(0, native.Length, (pointer, _) => call(pointer), Alignment);
        buffer.WithBytes(0, native.Length, bytes => { bytes.CopyTo(native); return 0; });
        return result;
    }

    private byte[] Validate(ReadOnlySpan<byte> image, byte[] native)
    {
        RequireCurrentProcess();
        if (image.Length < ImageLength)
            throw new ArgumentException($"The BASIC field image has {image.Length} bytes; this native struct requires {ImageLength}.", nameof(image));
        return native;
    }

    private byte[] Encode(ReadOnlySpan<byte> image)
    {
        var native = Validate(image, new byte[Size]);
        var slot = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var target = native.AsSpan(offsets[i]);
            switch (fields[i])
            {
                case NativeStructField.UInt64: image.Slice(slot, 8).CopyTo(target); slot += 8; continue;
                case NativeStructField.UIntPtrSaturating or NativeStructField.UIntPtrChecked when PointerSize == 8:
                    BinaryPrimitives.WriteUInt64LittleEndian(target, BinaryPrimitives.ReadUInt32LittleEndian(image[slot..])); break;
                default: image.Slice(slot, 4).CopyTo(target); break;
            }
            slot += 4;
        }
        return native;
    }

    private void Decode(byte[] native, Span<byte> image)
    {
        // Validate every narrowing before writing, so an overflow leaves the BASIC image unchanged.
        Span<uint> narrowed = stackalloc uint[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (PointerSize != 8 || fields[i] is not (NativeStructField.UIntPtrSaturating or NativeStructField.UIntPtrChecked)) continue;
            var value = BinaryPrimitives.ReadUInt64LittleEndian(native.AsSpan(offsets[i]));
            if (value > uint.MaxValue && fields[i] == NativeStructField.UIntPtrChecked)
                throw new OverflowException($"Native struct field {i} value {value} does not fit its 32-bit BASIC field.");
            narrowed[i] = (uint)Math.Min(value, uint.MaxValue);
        }
        var slot = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var source = native.AsSpan(offsets[i]);
            switch (fields[i])
            {
                case NativeStructField.UInt64: source[..8].CopyTo(image[slot..]); slot += 8; continue;
                case NativeStructField.UIntPtrSaturating or NativeStructField.UIntPtrChecked when PointerSize == 8:
                    BinaryPrimitives.WriteUInt32LittleEndian(image[slot..], narrowed[i]); break;
                default: source[..4].CopyTo(image[slot..]); break;
            }
            slot += 4;
        }
    }

    private void RequireCurrentProcess()
    {
        if (PointerSize != IntPtr.Size)
            throw new InvalidOperationException($"A {PointerSize * 8}-bit native struct layout cannot be called from a {IntPtr.Size * 8}-bit process.");
    }

    private int NativeSize(NativeStructField field) => field switch
    {
        NativeStructField.UInt64 => 8,
        NativeStructField.UIntPtrSaturating or NativeStructField.UIntPtrChecked => PointerSize,
        _ => 4
    };
}
