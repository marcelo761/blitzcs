using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

[CollectionDefinition("Native pointer ObjectBuffers", DisableParallelization = true)]
public sealed class NativePointerArgumentCollection
{
    public const string Name = "Native pointer ObjectBuffers";
}

[Collection(NativePointerArgumentCollection.Name)]
public sealed class NativePointerArgumentTests : IDisposable
{
    private const string ObjectBuffersKey = "__BlitzCS.ObjectBuffers";
    private static int nextToken;
    private readonly object? previousValue;
    private readonly ConcurrentDictionary<int, (byte[] Bytes, Action<byte[]> WriteBack)> objectBuffers;
    private readonly bool installedTable;
    private readonly NativeBlitzCommandLibrary library = NativeBlitzCommandLibrary.For(
        typeof(NativePointerArgumentTests).Assembly,
        $"unused-pointer-{Guid.NewGuid():N}.dll",
        NativeBlitzCommandLibrary.SupportedRuntimeSha256);

    public NativePointerArgumentTests()
    {
        previousValue = AppContext.GetData(ObjectBuffersKey);
        if (previousValue is ConcurrentDictionary<int, (byte[] Bytes, Action<byte[]> WriteBack)> existing)
        {
            objectBuffers = existing;
            return;
        }

        objectBuffers = new();
        AppContext.SetData(ObjectBuffersKey, objectBuffers);
        installedTable = true;
    }

    [Fact]
    public void PointerCopiesObjectBytesAndWritesBackOnlyWhenCompleted()
    {
        var original = new byte[] { 1, 2, 3, 4 };
        byte[]? writtenBack = null;
        var writeBackCount = 0;
        var token = AddObjectBuffer(original, bytes =>
        {
            writeBackCount++;
            writtenBack = bytes;
        });

        var pointer = library.Pointer(token);
        try
        {
            Assert.False(objectBuffers.ContainsKey(token));
            Assert.NotEqual(nint.Zero, pointer.Address);

            var copied = new byte[original.Length];
            Marshal.Copy(pointer.Address, copied, 0, copied.Length);
            Assert.Equal(original, copied);

            byte[] modified = [9, 8, 7, 6];
            Marshal.Copy(modified, 0, pointer.Address, modified.Length);
            Assert.Null(writtenBack);

            pointer.Complete();
            pointer.Complete();
            Assert.Equal(1, writeBackCount);
            Assert.Equal(modified, writtenBack);
        }
        finally
        {
            pointer.Dispose();
        }
    }

    [Fact]
    public void DisposeWithoutCompleteDoesNotWriteBackAndIsIdempotent()
    {
        var writeBackCount = 0;
        var token = AddObjectBuffer([1, 2], _ => writeBackCount++);
        var pointer = library.Pointer(token);
        Assert.NotEqual(nint.Zero, pointer.Address);

        pointer.Dispose();
        pointer.Dispose();

        Assert.Equal(0, writeBackCount);
        Assert.False(objectBuffers.ContainsKey(token));
    }

    [Fact]
    public void NullAndUnregisteredRawPointersPassThroughUnchanged()
    {
        using var nullPointer = library.Pointer(0);
        Assert.Equal(nint.Zero, nullPointer.Address);

        const int raw = 0x12345678;
        using var rawPointer = library.Pointer(raw);
        Assert.Equal((nint)(uint)raw, rawPointer.Address);
    }

    [Fact]
    public void EmptyObjectBufferGetsFourZeroedNativeBytes()
    {
        byte[]? writtenBack = null;
        var token = AddObjectBuffer([], bytes => writtenBack = bytes);

        using var pointer = library.Pointer(token);
        Assert.NotEqual(nint.Zero, pointer.Address);
        var minimum = new byte[4];
        Marshal.Copy(pointer.Address, minimum, 0, minimum.Length);
        Assert.Equal(new byte[4], minimum);

        pointer.Complete();
        Assert.Empty(Assert.IsType<byte[]>(writtenBack));
    }

    public void Dispose()
    {
        if (installedTable && ReferenceEquals(AppContext.GetData(ObjectBuffersKey), objectBuffers))
            AppContext.SetData(ObjectBuffersKey, previousValue);
    }

    private int AddObjectBuffer(byte[] bytes, Action<byte[]> writeBack)
    {
        var token = Interlocked.Decrement(ref nextToken);
        Assert.True(objectBuffers.TryAdd(token, (bytes, writeBack)));
        return token;
    }
}
