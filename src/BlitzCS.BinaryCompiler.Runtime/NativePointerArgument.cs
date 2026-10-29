using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>A native userlib pointer with optional temporary storage for a BASIC object's fields.</summary>
public readonly struct NativePointerArgument : IDisposable
{
    private readonly ObjectBuffer? buffer;
    public nint Address { get; }

    internal NativePointerArgument(nint address) => Address = address;

    private NativePointerArgument(ObjectBuffer buffer)
    {
        this.buffer = buffer;
        Address = buffer.Address;
    }

    internal static NativePointerArgument Copy(byte[] bytes, Action<byte[]> writeBack) => new(new ObjectBuffer(bytes, writeBack));

    /// <summary>Copies object fields back after a successful native call.</summary>
    public void Complete() => buffer?.Complete();

    public void Dispose() => buffer?.Dispose();

    private sealed unsafe class ObjectBuffer : IDisposable
    {
        private readonly byte[] bytes;
        private readonly Action<byte[]> writeBack;
        private nint address;
        private bool completed;
        internal nint Address => address;

        internal ObjectBuffer(byte[] bytes, Action<byte[]> writeBack)
        {
            this.bytes = bytes;
            this.writeBack = writeBack;
            address = (nint)NativeMemory.AllocZeroed((nuint)Math.Max(bytes.Length, 4));
            if (address == 0) throw new OutOfMemoryException();
            bytes.AsSpan().CopyTo(new Span<byte>((void*)address, bytes.Length));
        }

        internal void Complete()
        {
            if (address == 0) throw new ObjectDisposedException(nameof(NativePointerArgument));
            if (completed) return;
            new ReadOnlySpan<byte>((void*)address, bytes.Length).CopyTo(bytes);
            completed = true;
            writeBack(bytes);
        }

        public void Dispose()
        {
            var allocation = Interlocked.Exchange(ref address, 0);
            if (allocation != 0) NativeMemory.Free((void*)allocation);
        }
    }
}
