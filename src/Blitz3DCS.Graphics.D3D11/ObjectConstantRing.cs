namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Where one flush's object constants go inside the shared dynamic constant buffer.</summary>
/// <param name="Offset">Byte offset of the reserved region (a multiple of <see cref="ObjectConstantRing.Alignment"/>).</param>
/// <param name="Discard">Map with WRITE_DISCARD (renames the buffer); otherwise WRITE_NO_OVERWRITE.</param>
/// <param name="GrowTo">Non-zero when the buffer must be recreated with this many bytes before mapping.</param>
internal readonly record struct RingReservation(int Offset, bool Discard, int GrowTo);

/// <summary>
/// Pure allocator for the per-frame object constant buffer. Each flush of queued mesh draws reserves one region and
/// fills it with a single Map. Regions are appended with WRITE_NO_OVERWRITE (earlier regions may still be read by
/// in-flight draws, so they are never overwritten) and the ring wraps with WRITE_DISCARD. Offsets are 256-byte aligned
/// because the D3D11.1 *SetConstantBuffers1 first-constant must be a multiple of 16 constants of 16 bytes.
/// </summary>
internal sealed class ObjectConstantRing
{
    public const int Alignment = 256;
    public const int ConstantBytes = 16;
    /// <summary>Largest window one binding may expose (D3D11 limit: 4096 constants).</summary>
    public const int MaxWindowBytes = 4096 * ConstantBytes;

    private readonly bool _noOverwrite;
    private bool _fresh = true;

    public ObjectConstantRing(int initialCapacity, bool noOverwriteSupported)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        Capacity = Align(initialCapacity);
        _noOverwrite = noOverwriteSupported;
    }

    public int Capacity { get; private set; }

    /// <summary>First free byte after the most recent reservation.</summary>
    public int Cursor { get; private set; }

    public static int Align(int bytes) => checked(bytes + Alignment - 1) & ~(Alignment - 1);

    /// <summary>First-constant argument of *SetConstantBuffers1 for an aligned byte offset.</summary>
    public static uint FirstConstant(int offset) => (uint)(offset / ConstantBytes);

    /// <summary>Constant-count argument of *SetConstantBuffers1 (a multiple of 16) covering <paramref name="bytes"/>.</summary>
    public static uint ConstantCount(int bytes) => (uint)(Align(bytes) / ConstantBytes);

    public RingReservation Reserve(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var size = Align(bytes);
        if (size > Capacity)
        {
            var grown = Capacity;
            while (grown < size) grown = checked(grown * 2);
            Capacity = grown;
            _fresh = false;
            Cursor = size;
            return new RingReservation(0, Discard: true, GrowTo: grown);
        }
        if (_fresh || !_noOverwrite || Cursor > Capacity - size)
        {
            _fresh = false;
            Cursor = size;
            return new RingReservation(0, Discard: true, GrowTo: 0);
        }
        var offset = Cursor;
        Cursor += size;
        return new RingReservation(offset, Discard: false, GrowTo: 0);
    }

    /// <summary>The buffer was recreated or the device state lost: the next reservation must discard.</summary>
    public void Reset() { _fresh = true; Cursor = 0; }
}
