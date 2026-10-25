using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BlitzCS.NativeInterop;
using Xunit;

namespace BlitzCS.NativeInterop.Tests;

public sealed class NativeBufferTests
{
    [Fact]
    public void ResizePreservesPrefixAndZeroesNewBytes()
    {
        using var buffer = new NativeBuffer(4);
        buffer.WithBytes(0, 4, bytes => { bytes.Fill(42); return 0; });
        buffer.Resize(6);
        Assert.Equal(new byte[] { 42, 42, 42, 42, 0, 0 }, buffer.WithBytes(0, 6, bytes => bytes.ToArray()));
        buffer.Resize(2);
        buffer.Resize(4);
        Assert.Equal(new byte[] { 42, 42, 0, 0 }, buffer.WithBytes(0, 4, bytes => bytes.ToArray()));
        Assert.Equal(4, buffer.ByteLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Resize(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBuffer(-1));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(5, 0)]
    [InlineData(4, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void BothBorrowFormsRejectInvalidRanges(int offset, int count)
    {
        using var buffer = new NativeBuffer(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.WithBytes(offset, count, bytes => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.WithPointer(offset, count, (pointer, length) => 0));
    }

    [Fact]
    public void EmptyRangesIncludeEndAndUseNullNativePointers()
    {
        using var buffer = new NativeBuffer(4);
        Assert.Equal(0, buffer.WithBytes(4, 0, bytes => bytes.Length));
        Assert.Equal((nint)0, buffer.WithPointer(4, 0, (pointer, length) => { Assert.Equal(0, length); return pointer; }, 16));
        buffer.Resize(0);
        Assert.Equal((nint)0, buffer.WithPointer(0, 0, (pointer, length) => pointer));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    public void AlignmentMustBePositivePowerOfTwo(int alignment)
    {
        using var buffer = new NativeBuffer(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.WithPointer(0, 4, (pointer, length) => 0, alignment));
    }

    [Fact]
    public void AlignmentChecksTheActualAddressBeforeCallingNativeCode()
    {
        using var buffer = new NativeBuffer(16);
        // Adjacent bytes cannot both satisfy a two-byte alignment requirement.
        var rejected = 0;
        for (var offset = 0; offset < 2; offset++)
        {
            try
            {
                buffer.WithPointer(offset, 1, (pointer, length) => { Assert.Equal((nint)0, pointer % 2); return 0; }, 2);
            }
            catch (ArgumentException) { rejected++; }
        }
        Assert.Equal(1, rejected);
        Assert.Equal(16, buffer.ByteLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveBorrowRejectsAllReentrantMutationsAndBorrows(bool usePointer)
    {
        using var buffer = new NativeBuffer(4);
        int Check()
        {
            Assert.Equal(4, buffer.ByteLength);
            Assert.Throws<InvalidOperationException>(() => buffer.WithBytes(0, 1, bytes => 0));
            Assert.Throws<InvalidOperationException>(() => buffer.WithPointer(0, 1, (pointer, length) => 0));
            Assert.Throws<InvalidOperationException>(() => buffer.Resize(8));
            Assert.Throws<InvalidOperationException>(buffer.Dispose);
            return 42;
        }
        Assert.Equal(42, usePointer ? buffer.WithPointer(0, 4, (pointer, length) => Check()) : buffer.WithBytes(0, 4, bytes => Check()));
        buffer.Resize(8);
        Assert.Equal(8, buffer.ByteLength);
    }

    [Fact]
    public void CallbackExceptionsLeaveBufferUsableAndDisposeIsIdempotent()
    {
        var buffer = new NativeBuffer(4);
        var failure = new ApplicationException("callback failure");
        Assert.Same(failure, Assert.Throws<ApplicationException>(() => buffer.WithBytes<int>(0, 4, bytes => throw failure)));
        Assert.Same(failure, Assert.Throws<ApplicationException>(() => buffer.WithPointer<int>(0, 4, (pointer, length) => throw failure)));
        buffer.Resize(8);
        buffer.WithPointer(2, 1, (pointer, length) => { Marshal.WriteByte(pointer, 37); return 0; });
        Assert.Equal(37, buffer.WithBytes(2, 1, bytes => (int)bytes[0]));
        buffer.Dispose();
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.ByteLength);
        Assert.Throws<ObjectDisposedException>(() => buffer.Resize(1));
        Assert.Throws<ObjectDisposedException>(() => buffer.WithBytes(0, 0, bytes => 0));
        Assert.Throws<ObjectDisposedException>(() => buffer.WithPointer(0, 0, (pointer, length) => 0));
    }

    [Fact]
    public void BothBorrowFormsRejectNullCallbacks()
    {
        using var buffer = new NativeBuffer(1);
        Assert.Throws<ArgumentNullException>(() => buffer.WithBytes<int>(0, 1, null!));
        Assert.Throws<ArgumentNullException>(() => buffer.WithPointer<int>(0, 1, null!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResizeAndDisposeWaitForBorrowOnAnotherThread(bool usePointer, bool dispose)
    {
        using var buffer = new NativeBuffer(4);
        using var started = new ManualResetEventSlim();
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            started.Set();
            try
            {
                if (dispose) buffer.Dispose();
                else buffer.Resize(8);
            }
            catch (Exception error) { workerFailure = error; }
        });
        int Borrow()
        {
            worker.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() =>
                (worker.ThreadState & ThreadState.WaitSleepJoin) != 0 || !worker.IsAlive,
                TimeSpan.FromSeconds(5)));
            Assert.True(worker.IsAlive);
            Assert.Equal(4, buffer.ByteLength);
            return 0;
        }
        try
        {
            if (usePointer) buffer.WithPointer(0, 4, (pointer, length) => Borrow());
            else buffer.WithBytes(0, 4, bytes => Borrow());
        }
        finally
        {
            if (worker.ThreadState != ThreadState.Unstarted)
                Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
        Assert.Null(workerFailure);
        if (dispose) Assert.Throws<ObjectDisposedException>(() => buffer.ByteLength);
        else Assert.Equal(8, buffer.ByteLength);
    }

    [Fact]
    public void RealCWritesRectIntoScopedBufferWithDeclaredAbiAndLayout()
    {
        using var library = NativeLibraryOwner.Load(Path.Combine(AppContext.BaseDirectory, "native", "libblitzcs_native_buffer.so"));
        var layout = library.Bind<Layout>("blitz_rect_layout");
        var fill = library.Bind<FillRect>("blitz_rect_fill");
        Assert.Equal((nuint)16, layout.Invoke(call => call(0)));
        var alignment = checked((int)layout.Invoke(call => call(1)));
        Assert.Equal(4, alignment);
        for (var field = 0; field < 4; field++)
            Assert.Equal((nuint)(field * 4), layout.Invoke(call => call(field + 2)));
        using var buffer = new NativeBuffer(24);
        buffer.WithBytes(0, 24, bytes => { bytes.Fill(0xCC); return 0; });
        Assert.Equal(1, buffer.WithPointer(4, 16, (pointer, length) => fill.Invoke(call => call(pointer, (nuint)length)), alignment));
        buffer.WithBytes(0, 24, bytes =>
        {
            Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, bytes[..4].ToArray());
            Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, bytes[20..].ToArray());
            Assert.True(BitConverter.IsLittleEndian);
            Assert.Equal(0x01020304, BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]));
            Assert.Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]));
            Assert.Equal(300, BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]));
            Assert.Equal(-400, BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]));
            return 0;
        });
        Assert.Equal(0, buffer.WithPointer(0, 15, (pointer, length) => fill.Invoke(call => call(pointer, (nuint)length)), alignment));
        Assert.Equal(0, buffer.WithPointer(24, 0, (pointer, length) => fill.Invoke(call => call(pointer, (nuint)length))));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Layout(int field);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FillRect(nint pointer, nuint byteLength);
}
