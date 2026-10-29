using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BlitzCS.CLI;
using BlitzCS.Compiler;
using BlitzCS.NativeInterop.ManagedBinding;
using Xunit;

namespace BlitzCS.NativeInterop.Tests;

public sealed class NativeStructLayoutTests : IDisposable
{
    private static readonly string BufferLibrary = Path.Combine(AppContext.BaseDirectory, "native", "libblitzcs_native_buffer.so");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-struct-" + Guid.NewGuid());

    // In-process calls resolve the fixture through the same variable as the child BASIC process.
    static NativeStructLayoutTests() => Environment.SetEnvironmentVariable("BLITZCS_NATIVEINTEROP_BUFFER_LIBRARY", BufferLibrary);

    public NativeStructLayoutTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void TargetLayoutsFollowWin32AndWin64NaturalAlignment()
    {
        NativeStructField[] memoryStatus = [NativeStructField.UInt32, NativeStructField.UInt32, .. Enumerable.Repeat(NativeStructField.UIntPtrSaturating, 6)];
        var win32 = NativeStructLayout.Sequential(4, memoryStatus);
        Assert.Equal((32, 4, 32), (win32.Size, win32.Alignment, win32.ImageLength));
        Assert.Equal([0, 4, 8, 12, 16, 20, 24, 28], win32.Offsets);
        var win64 = NativeStructLayout.Sequential(8, memoryStatus);
        Assert.Equal((56, 8, 32), (win64.Size, win64.Alignment, win64.ImageLength));
        Assert.Equal([0, 4, 8, 16, 24, 32, 40, 48], win64.Offsets);

        // MEMORYSTATUSEX: DWORDLONG fields occupy two BASIC slots on both targets.
        NativeStructField[] memoryStatusEx = [NativeStructField.UInt32, NativeStructField.UInt32, .. Enumerable.Repeat(NativeStructField.UInt64, 7)];
        foreach (var pointerSize in new[] { 4, 8 })
        {
            var ex = NativeStructLayout.Sequential(pointerSize, memoryStatusEx);
            Assert.Equal((64, 8, 64), (ex.Size, ex.Alignment, ex.ImageLength));
        }

        var other = NativeStructLayout.Sequential(IntPtr.Size == 8 ? 4 : 8, NativeStructField.Int32);
        Assert.Throws<InvalidOperationException>(() => other.Invoke(new byte[4], NativeStructDirection.InOut, _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeStructLayout.Sequential(2, NativeStructField.Int32));
        Assert.Throws<ArgumentException>(() => NativeStructLayout.Sequential());
    }

    [Fact]
    public void LayoutMatchesTheCompiledCStruct()
    {
        using var library = NativeLibraryOwner.Load(BufferLibrary);
        var layout = library.Bind<Layout>("blitz_status_layout");
        var status = NativeStructBindings.Status;
        Assert.Equal((nuint)status.Size, layout.Invoke(call => call(0)));
        Assert.Equal((nuint)status.Alignment, layout.Invoke(call => call(1)));
        for (var field = 0; field < status.Offsets.Count; field++)
            Assert.Equal((nuint)status.Offsets[field], layout.Invoke(call => call(field + 2)));
    }

    [Fact]
    public void RealCStructHonorsInOutAndInOutDirections()
    {
        var image = Image(input: 20, scale: 2.5f, small: 7, wideLow: -1, wideHigh: 0);
        Assert.Equal(20, NativeStructBindings.StatusInOut(image));
        Assert.Equal(NativeStructBindings.Status.Size, Slot(image, 0));
        Assert.Equal(20, Slot(image, 1));
        Assert.Equal(-1, Slot(image, 3)); // SIZE_MAX saturates to 0xFFFFFFFF on 64-bit; identical on 32-bit.
        Assert.Equal(8, Slot(image, 4));
        Assert.Equal((0, 1), (Slot(image, 5), Slot(image, 6))); // 64-bit carry crosses the two slots.
        Assert.Equal(42, Slot(image, 7));

        var unchanged = Image(input: 5, scale: 0, small: 0, wideLow: 0, wideHigh: 0);
        var before = unchanged.ToArray();
        Assert.Equal(5, NativeStructBindings.StatusIn(unchanged));
        Assert.Equal(before, unchanged);

        var outImage = Image(input: 99, scale: 9, small: 3, wideLow: 3, wideHigh: 3);
        Assert.Equal(0, NativeStructBindings.StatusOut(outImage)); // The callee observes a zeroed struct.
        Assert.Equal((1, 1, 0, 0), (Slot(outImage, 4), Slot(outImage, 5), Slot(outImage, 6), Slot(outImage, 7)));

        Assert.Equal(-1, NativeStructBindings.StatusInOut(Span<byte>.Empty)); // BASIC Null is a NULL pointer.
        var error = Assert.Throws<ArgumentException>(() => NativeStructBindings.StatusInOut(new byte[28]));
        Assert.Contains("requires 32", error.Message);
    }

    [Fact]
    public void CheckedPointerSizedNarrowingFailsWithoutPartialCopyBack()
    {
        if (IntPtr.Size != 8) return;
        var image = Image(input: 1, scale: 0, small: -1, wideLow: 0, wideHigh: 0);
        var before = image.ToArray();
        Assert.Throws<OverflowException>(() => NativeStructBindings.StatusInOut(image));
        Assert.Equal(before, image);
    }

    [Fact]
    public async Task BasicTypeInstancesCrossTheRealNativeStructBoundary()
    {
        var registry = new ExternalFunctionRegistry([
            Function("StatusIn", nameof(NativeStructBindings.StatusIn)),
            Function("StatusOut", nameof(NativeStructBindings.StatusOut)),
            Function("StatusInOut", nameof(NativeStructBindings.StatusInOut))
        ]);
        var source = """
            Type Status
                Field length%, input%, scale#, big%, small%, wideLow%, wideHigh%, output%
            End Type
            s.Status = New Status
            s\input = 20 : s\scale = 2.5 : s\small = 7 : s\wideLow = -1
            Print StatusInOut(s)
            Print s\length + "," + s\big + "," + s\small + "," + s\wideLow + "," + s\wideHigh + "," + s\output
            s\input = 3
            Print StatusIn(s) + "," + s\output
            Print StatusOut(s) + "," + s\input + "," + s\small
            Print StatusInOut(Null)
            """;
        var generated = BasicCompiler.Compile(source, "struct.bb", registry);
        var assembly = Path.Combine(directory, "StructProgram.dll");
        DotNetEmitter.Emit(generated, assembly, "BlitzProgram", registry.ReferencePaths);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        start.Environment["BLITZCS_NATIVEINTEROP_BUFFER_LIBRARY"] = BufferLibrary;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("", await stderr);
        Assert.Equal(0, process.ExitCode);
        var n = Environment.NewLine;
        Assert.Equal($"20{n}{NativeStructBindings.Status.Size},-1,8,0,1,42{n}3,42{n}0,0,1{n}-1{n}", await stdout);
    }

    private static ExternalFunction Function(string name, string method) =>
        new(name, typeof(NativeStructBindings).GetMethod(method, BindingFlags.Public | BindingFlags.Static)!);

    private static byte[] Image(int input, float scale, int small, int wideLow, int wideHigh)
    {
        var image = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4), input);
        BinaryPrimitives.WriteSingleLittleEndian(image.AsSpan(8), scale);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(16), small);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(20), wideLow);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(24), wideHigh);
        return image;
    }

    private static int Slot(byte[] image, int slot) => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(slot * 4));

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Layout(int field);
}
