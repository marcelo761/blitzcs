using System.Buffers.Binary;
using BlitzCS.NativeInterop;

namespace BlitzCS.LegacyRuntime;

/// <summary>Owns legacy memory banks through checked, positive, non-reused Int32 tokens.</summary>
/// <remarks>Tokens are not addresses and are meaningful only to the issuing bank store.
/// A store serializes operations, including synchronous native borrows. Host callbacks must
/// not wait for other threads using this store or retain a borrowed native address.</remarks>
public sealed class BankStore : IDisposable
{
    private static int nextHandle = 0x3fffffff;
    private readonly object gate = new();
    private readonly Dictionary<int, NativeBuffer> banks = [];
    private readonly int maximumBankSize;
    private int borrows;
    private bool disposed;

    public BankStore() : this(Array.MaxLength) { }

    public BankStore(int maximumBankSize)
    {
        if (maximumBankSize < 0 || maximumBankSize > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(maximumBankSize));
        this.maximumBankSize = maximumBankSize;
    }

    public int LiveBankCount { get { lock (gate) return banks.Count; } }

    public int CreateBank(int size = 0)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ValidateSize(size);
            var handle = NextHandle();
            banks.Add(handle, new NativeBuffer(size));
            return handle;
        }
    }

    public void FreeBank(int handle)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!banks.TryGetValue(handle, out var bank)) return;
            // Dispose first: a reentrant free must leave the active token intact.
            bank.Dispose();
            banks.Remove(handle);
        }
    }

    public int BankSize(int handle) { lock (gate) return Get(handle).ByteLength; }

    public void ResizeBank(int handle, int size)
    {
        lock (gate)
        {
            var bank = Get(handle);
            ValidateSize(size);
            bank.Resize(size);
        }
    }

    public int PeekByte(int handle, int offset) => WithBytes(handle, offset, 1, bytes => bytes[0]);
    public int PeekShort(int handle, int offset) => WithBytes(handle, offset, 2, bytes => BinaryPrimitives.ReadUInt16LittleEndian(bytes));
    public int PeekInt(int handle, int offset) => WithBytes(handle, offset, 4, bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes));
    public float PeekFloat(int handle, int offset) => WithBytes(handle, offset, 4, bytes => BinaryPrimitives.ReadSingleLittleEndian(bytes));
    public void PokeByte(int handle, int offset, int value) => WithBytes(handle, offset, 1, bytes => { bytes[0] = unchecked((byte)value); return 0; });
    public void PokeShort(int handle, int offset, int value) => WithBytes(handle, offset, 2, bytes => { BinaryPrimitives.WriteUInt16LittleEndian(bytes, unchecked((ushort)value)); return 0; });
    public void PokeInt(int handle, int offset, int value) => WithBytes(handle, offset, 4, bytes => { BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return 0; });
    public void PokeFloat(int handle, int offset, float value) => WithBytes(handle, offset, 4, bytes => { BinaryPrimitives.WriteSingleLittleEndian(bytes, value); return 0; });

    public void CopyBank(int sourceHandle, int sourceOffset, int destinationHandle, int destinationOffset, int count)
    {
        lock (gate)
        {
            var source = Get(sourceHandle);
            var destination = Get(destinationHandle);
            ValidateRange(source.ByteLength, sourceOffset, count);
            ValidateRange(destination.ByteLength, destinationOffset, count);
            if (ReferenceEquals(source, destination))
            {
                source.WithBytes(0, source.ByteLength, bytes =>
                {
                    bytes.Slice(sourceOffset, count).CopyTo(bytes.Slice(destinationOffset, count));
                    return 0;
                });
                return;
            }
            // A bounded snapshot avoids retaining spans or nesting cross-buffer locks.
            var copy = source.WithBytes(sourceOffset, count, bytes => bytes.ToArray());
            destination.WithBytes(destinationOffset, count, bytes => { copy.CopyTo(bytes); return 0; });
        }
    }

    public TResult WithBytes<TResult>(int handle, int offset, int count, NativeBufferOperation<TResult> operation)
    {
        lock (gate)
        {
            var bank = Get(handle);
            borrows++;
            try { return bank.WithBytes(offset, count, operation); }
            finally { borrows--; }
        }
    }

    /// <summary>Lends actual owned bank bytes to a trusted native adapter, never to BASIC.</summary>
    public TResult WithPointer<TResult>(int handle, int offset, int count, Func<nint, int, TResult> operation, int alignment = 1)
    {
        lock (gate)
        {
            var bank = Get(handle);
            borrows++;
            try { return bank.WithPointer(offset, count, operation, alignment); }
            finally { borrows--; }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ThrowIfBorrowed();
            ReleaseBanks();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            ThrowIfBorrowed();
            ReleaseBanks();
            disposed = true;
        }
    }

    private void ReleaseBanks()
    {
        foreach (var bank in banks.Values) bank.Dispose();
        banks.Clear();
    }

    private NativeBuffer Get(int handle)
    {
        ThrowIfDisposed();
        return banks.TryGetValue(handle, out var bank) ? bank : throw new InvalidOperationException("bbBank does not exist.");
    }

    private void ValidateSize(int size)
    {
        if (size < 0 || size > maximumBankSize)
            throw new ArgumentOutOfRangeException(nameof(size), $"Bank size must be between 0 and {maximumBankSize} bytes.");
    }

    private static void ValidateRange(int length, int offset, int count)
    {
        if (offset < 0 || offset > length || count < 0 || count > length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "Bank range is outside its byte length.");
    }

    private static int NextHandle()
    {
        // High positive tokens preserve legacy positive-bank checks without exposing addresses.
        while (true)
        {
            var current = Volatile.Read(ref nextHandle);
            if (current == int.MaxValue) throw new InvalidOperationException("Bank handle token space is exhausted.");
            if (Interlocked.CompareExchange(ref nextHandle, current + 1, current) == current) return current + 1;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void ThrowIfBorrowed()
    {
        if (borrows != 0) throw new InvalidOperationException("Banks cannot be cleared or disposed during an active borrow.");
    }
}
