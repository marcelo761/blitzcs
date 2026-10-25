using System.Runtime.InteropServices;

namespace BlitzCS.NativeInterop;

/// <summary>A synchronous operation over a borrowed slice of an owned byte buffer.</summary>
public delegate TResult NativeBufferOperation<TResult>(Span<byte> bytes);

/// <summary>Owns managed bytes and lends bounded slices to synchronous managed or native operations.</summary>
/// <remarks>Borrow callbacks run under the buffer's lifetime lock. They must complete synchronously
/// and must not wait for other threads to access the same buffer. Native callbacks are trusted host
/// code: they must obey the supplied byte count and must not retain or return the address, nor expose
/// it to BASIC. A native address is pinned only until its callback returns.</remarks>
public sealed class NativeBuffer : IDisposable
{
    private readonly object gate = new();
    private byte[]? bytes;
    private bool borrowed;

    public NativeBuffer(int byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        bytes = new byte[byteLength];
    }

    public int ByteLength
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return bytes!.Length;
            }
        }
    }

    /// <summary>Changes the byte length, preserving the prefix and zeroing newly added bytes.</summary>
    public void Resize(int byteLength)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ThrowIfBorrowed();
            ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
            Array.Resize(ref bytes, byteLength);
        }
    }

    public TResult WithBytes<TResult>(int offset, int count, NativeBufferOperation<TResult> operation)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ThrowIfBorrowed();
            ArgumentNullException.ThrowIfNull(operation);
            ValidateRange(offset, count);
            borrowed = true;
            try { return operation(bytes.AsSpan(offset, count)); }
            finally { borrowed = false; }
        }
    }

    /// <summary>Pins a bounded slice for one synchronous native operation; an empty slice has address zero.</summary>
    /// <remarks>Alignment must be a positive power of two and the actual slice address must satisfy it.
    /// An incompatible address is rejected; this buffer does not provide aligned allocation.</remarks>
    public TResult WithPointer<TResult>(int offset, int count, Func<nint, int, TResult> operation, int alignment = 1)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ThrowIfBorrowed();
            ArgumentNullException.ThrowIfNull(operation);
            ValidateRange(offset, count);
            if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a positive power of two.");
            borrowed = true;
            GCHandle pin = default;
            try
            {
                if (count == 0) return operation(0, 0);
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                var pointer = pin.AddrOfPinnedObject() + offset;
                if ((nuint)pointer % (nuint)alignment != 0)
                    throw new ArgumentOutOfRangeException(nameof(alignment), "The slice address does not satisfy the requested alignment.");
                return operation(pointer, count);
            }
            finally
            {
                if (pin.IsAllocated) pin.Free();
                borrowed = false;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            ThrowIfBorrowed();
            bytes = null;
        }
    }

    private void ValidateRange(int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > bytes!.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset exceeds the buffer length.");
        if (count > bytes.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "Count exceeds the remaining buffer length.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(bytes is null, this);

    private void ThrowIfBorrowed()
    {
        if (borrowed)
            throw new InvalidOperationException("The buffer cannot be resized, disposed, or borrowed again from inside an active borrow.");
    }
}
