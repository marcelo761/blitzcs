using System.Buffers.Binary;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Access to the guest's x87 stack from a managed import. Some CRT entry points take their argument in ST0 (<c>_ftol</c>, <c>_CIpow</c> ...): the guest's x87
/// registers are the real ones of the executing thread and survive the managed transition, so a 12-byte machine-code helper pops ST0 into memory.
/// <see cref="GuestCall.TryPopSt0"/> must be called from inside an import handler on the thread running the guest call.
/// </summary>
public static unsafe class GuestX87
{
    // fxam; fnstsw ax; and eax,0x4500; cmp eax,0x4100 (ST0 empty); je empty; fstp tbyte [rdi]; xor eax,eax; ret; empty: mov eax,1; ret   (System V: rdi = destination)
    private static readonly byte[] Code =
    [
        0xD9, 0xE5, 0xDF, 0xE0, 0x25, 0x00, 0x45, 0x00, 0x00, 0x3D, 0x00, 0x41, 0x00, 0x00, 0x74, 0x05,
        0xDB, 0x3F, 0x31, 0xC0, 0xC3, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,
    ];

    private static nint helper;
    private static readonly object Gate = new();

    private static nint Helper()
    {
        if (helper != 0) return helper;
        lock (Gate)
        {
            if (helper != 0) return helper;
            var page = Libc.Map(0, (nuint)Libc.PageSize, Libc.PROT_READ | Libc.PROT_WRITE, 0, out var errno);
            if (page == Libc.MapFailed) throw new GuestLoadException($"mmap for the x87 helper failed with errno {errno}.");
            Code.CopyTo(new Span<byte>((void*)page, Code.Length));
            Libc.Protect(page, (nuint)Libc.PageSize, Libc.PROT_READ | Libc.PROT_EXEC);
            helper = page;
            return helper;
        }
    }

    /// <summary>Pops ST0 into <paramref name="extended80"/> (the 10-byte image FSTP m80 stores). False when the x87 stack is empty (nothing is written).</summary>
    internal static bool TryPop(Span<byte> extended80)
    {
        if (extended80.Length != 10) throw new ArgumentException("An x87 extended-precision image is exactly 10 bytes.", nameof(extended80));
        var scratch = stackalloc byte[16];
        var ok = ((delegate* unmanaged<byte*, int>)Helper())(scratch) == 0;
        if (ok) new Span<byte>(scratch, 10).CopyTo(extended80);
        return ok;
    }

    /// <summary>Converts an 80-bit extended image to double (round to nearest even; overflow gives infinity).</summary>
    public static double ToDouble(ReadOnlySpan<byte> extended80)
    {
        var mantissa = BinaryPrimitives.ReadUInt64LittleEndian(extended80);
        var se = BinaryPrimitives.ReadUInt16LittleEndian(extended80[8..]);
        var negative = (se & 0x8000) != 0;
        var exp = se & 0x7FFF;
        if (exp == 0x7FFF) return (mantissa << 1) == 0 ? (negative ? double.NegativeInfinity : double.PositiveInfinity) : double.NaN;
        if (mantissa == 0) return negative ? -0.0 : 0.0;
        // value = mantissa * 2^(exp - 16383 - 63); keep the top 53 bits and round the dropped 11 to nearest even
        var rest = mantissa & 0x7FF;
        var m53 = mantissa >> 11;
        if (rest > 0x400 || (rest == 0x400 && (m53 & 1) != 0)) m53++;
        var value = Math.ScaleB((double)m53, (exp == 0 ? 1 : exp) - 16383 - 63 + 11);
        return negative ? -value : value;
    }

    /// <summary>Converts a double to the 10-byte extended image (exact: every double is representable).</summary>
    public static byte[] FromDouble(double value)
    {
        var image = new byte[10];
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var sign = (ushort)((bits >> 63) != 0 ? 0x8000 : 0);
        var e = (int)(bits >> 52 & 0x7FF);
        var frac = bits & 0xFFFFFFFFFFFFFUL;
        ulong mantissa; int exp;
        if (e == 0x7FF) { exp = 0x7FFF; mantissa = 0x8000000000000000UL | (frac << 11); }
        else if (e == 0) 
        {
            if (frac == 0) { mantissa = 0; exp = 0; }
            else
            {
                var shift = System.Numerics.BitOperations.LeadingZeroCount(frac); // normalise the subnormal
                mantissa = frac << shift; exp = 1 - 1023 + 16383 - (shift - 11);
            }
        }
        else { mantissa = 0x8000000000000000UL | (frac << 11); exp = e - 1023 + 16383; }
        BinaryPrimitives.WriteUInt64LittleEndian(image, mantissa);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8), (ushort)(sign | exp));
        return image;
    }
}
