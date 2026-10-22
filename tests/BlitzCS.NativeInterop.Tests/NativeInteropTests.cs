using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text;
using BlitzCS.CLI;
using BlitzCS.Compiler;
using BlitzCS.NativeInterop.ManagedBinding;
using BlitzCS.NativeInterop;
using Xunit;

namespace BlitzCS.NativeInterop.Tests;

public sealed class NativeInteropTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blitzcs-native-" + Guid.NewGuid());
    private readonly string libraryPath = Path.Combine(AppContext.BaseDirectory, "native", "libblitzcs_native_demo.so");

    public NativeInteropTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void CallsRealCExportsWithTypedCdeclBindingsAndOwnedUtf8Resource()
    {
        using var library = NativeLibraryOwner.Load(libraryPath);
        var add = library.Bind<Add>("blitz_add");
        var scale = library.Bind<Scale>("blitz_scale");
        var signedByte = library.Bind<SignedByte>("blitz_signed_byte");
        var copy = library.Bind<Utf8Copy>("blitz_utf8_copy");
        var make = library.Bind<MakeMessage>("blitz_make_message");
        var free = library.Bind<NativeUtf8Resource.FreeResource>("blitz_free");

        Assert.Equal(42, add.Invoke(call => call(20, 22)));
        Assert.Equal(3.5f, scale.Invoke(call => call(1.75f, 2f)));
        Assert.Equal((sbyte)-1, signedByte.Invoke(call => call(255)));
        var original = System.Text.Encoding.UTF8.GetBytes("Olá 🌱");
        var inputPointer = Marshal.AllocHGlobal(original.Length);
        var outputPointer = Marshal.AllocHGlobal(original.Length);
        var copied = new byte[original.Length];
        Marshal.Copy(original, 0, inputPointer, original.Length);
        try
        {
            Assert.Equal((nuint)original.Length, copy.Invoke(call => call(inputPointer, (nuint)original.Length, outputPointer, (nuint)original.Length)));
            Marshal.Copy(outputPointer, copied, 0, copied.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPointer);
            Marshal.FreeHGlobal(inputPointer);
        }
        Assert.Equal(original, copied);

        var resourceInput = Marshal.AllocHGlobal(original.Length);
        Marshal.Copy(original, 0, resourceInput, original.Length);
        try
        {
            using var resource = make.Invoke(call =>
            {
                var pointer = call(resourceInput, (nuint)original.Length, out var length);
                return NativeUtf8Resource.Own(library, free, pointer, length);
            });
            Assert.Throws<InvalidOperationException>(library.Dispose);
            Assert.Equal("native:Olá 🌱", resource.Read());
        }
        finally { Marshal.FreeHGlobal(resourceInput); }
    }

    [Fact]
    public void MalformedNativeUtf8IsRejectedAndStillFreed()
    {
        using var library = NativeLibraryOwner.Load(libraryPath);
        var makeInvalid = library.Bind<MakeInvalidMessage>("blitz_make_invalid_message");
        var free = library.Bind<NativeUtf8Resource.FreeResource>("blitz_free");
        var count = library.Bind<LiveResourceCount>("blitz_live_resource_count");
        Assert.Equal(0, count.Invoke(call => call()));

        Assert.Throws<DecoderFallbackException>(() =>
        {
            using var resource = makeInvalid.Invoke(call =>
            {
                var pointer = call(out var length);
                return NativeUtf8Resource.Own(library, free, pointer, length);
            });
            resource.Read();
        });

        Assert.Equal(0, count.Invoke(call => call()));
    }

    [Fact]
    public void ManagedFacadeRejectsInvalidUtf16InsteadOfReplacingIt()
    {
        var previous = Environment.GetEnvironmentVariable("BLITZCS_NATIVEINTEROP_LIBRARY");
        Environment.SetEnvironmentVariable("BLITZCS_NATIVEINTEROP_LIBRARY", libraryPath);
        try { Assert.Throws<EncoderFallbackException>(() => NativeDemoBindings.Utf8ByteLength("\ud800")); }
        finally { Environment.SetEnvironmentVariable("BLITZCS_NATIVEINTEROP_LIBRARY", previous); }
    }

    [Fact]
    public void MissingExportReportsSymbolAndLibraryPath()
    {
        using var library = NativeLibraryOwner.Load(libraryPath);
        var error = Assert.Throws<NativeExportException>(() => library.Bind<Add>("blitz_does_not_exist"));
        Assert.Contains("blitz_does_not_exist", error.Message);
        Assert.Contains(libraryPath, error.Message);
    }

    [Fact]
    public void RejectsWrongBinaryFormatBeforePlatformLoader()
    {
        var path = Path.Combine(directory, "not-a-library.bin");
        File.WriteAllBytes(path, new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' });
        var error = Assert.Throws<NativeLibraryLoadException>(() => NativeLibraryOwner.Load(path));
        Assert.Contains(path, error.Message);
        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsWrongArchitectureFromElfHeader()
    {
        var path = Path.Combine(directory, "wrong-architecture.so");
        var header = new byte[64];
        header[0] = 0x7f;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = (byte)(Environment.Is64BitProcess ? 2 : 1);
        header[5] = 1;
        header[18] = (byte)(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 183 : 62);
        header[19] = 0;
        File.WriteAllBytes(path, header);
        var error = Assert.Throws<NativeLibraryLoadException>(() => NativeLibraryOwner.Load(path));
        Assert.Contains("architecture", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisposedLibraryRejectsCallsAndDisposeInsideInvocationFailsClearly()
    {
        var library = NativeLibraryOwner.Load(libraryPath);
        var add = library.Bind<Add>("blitz_add");
        Assert.Throws<InvalidOperationException>(() => add.Invoke(call =>
        {
            library.Dispose();
            return call(1, 2);
        }));
        library.Dispose();
        Assert.Throws<ObjectDisposedException>(() => add.Invoke(call => call(1, 2)));
    }

    [Fact]
    public async Task DisposalWaitsUntilAnInvocationLeavesItsScope()
    {
        var library = NativeLibraryOwner.Load(libraryPath);
        var add = library.Bind<Add>("blitz_add");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var invocation = Task.Run(() => add.Invoke(call =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return call(20, 22);
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        using var disposeStarted = new ManualResetEventSlim();
        var dispose = Task.Factory.StartNew(() =>
        {
            disposeStarted.Set();
            library.Dispose();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var lifetime = (ReaderWriterLockSlim)typeof(NativeLibraryOwner)
            .GetField("lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(library)!;
        try
        {
            Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => lifetime.WaitingWriteCount > 0 || dispose.IsCompleted, TimeSpan.FromSeconds(5)));
            Assert.False(dispose.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(42, await invocation);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ConcurrentNativeResourceOwnersKeepLibraryLoadedUntilEveryFreeCompletes()
    {
        var library = NativeLibraryOwner.Load(libraryPath);
        var make = library.Bind<MakeMessage>("blitz_make_message");
        var free = library.Bind<NativeUtf8Resource.FreeResource>("blitz_free");
        var input = Marshal.AllocHGlobal(1);
        Marshal.WriteByte(input, (byte)'x');
        var resources = new ConcurrentBag<NativeUtf8Resource>();
        try
        {
            Parallel.For(0, 64, _ => resources.Add(make.Invoke(call =>
            {
                var pointer = call(input, 1, out var length);
                return NativeUtf8Resource.Own(library, free, pointer, length);
            })));
            var error = Assert.Throws<InvalidOperationException>(library.Dispose);
            Assert.Contains("64 native resource", error.Message);
            Parallel.ForEach(resources, resource => resource.Dispose());
            library.Dispose();
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            library.Dispose();
        }
    }

    [Fact]
    public async Task RegisteredManagedFacadeRunsBASICAgainstRealNativeLibrary()
    {
        var registry = new ExternalFunctionRegistry([
            Function("NativeAdd", nameof(NativeDemoBindings.Add), typeof(int), typeof(int)),
            Function("NativeScale", nameof(NativeDemoBindings.Scale), typeof(float), typeof(float)),
            Function("NativeSignedByte", nameof(NativeDemoBindings.SignedByte), typeof(int)),
            Function("NativeUtf8Bytes", nameof(NativeDemoBindings.Utf8ByteLength), typeof(string)),
            Function("NativeMessage", nameof(NativeDemoBindings.Message), typeof(string)),
            Function("NativeClose", nameof(NativeDemoBindings.Close))
        ]);
        var generated = BasicCompiler.Compile("Print NativeAdd(20,22)\nPrint NativeScale(1.75,2.0)\nPrint NativeSignedByte(255)\nPrint NativeUtf8Bytes(\"Olá 🌱\")\nPrint NativeMessage$(\"mundo\")\nPrint NativeClose()", "native.bb", registry);
        var assembly = Path.Combine(directory, "NativeProgram.dll");
        DotNetEmitter.Emit(generated, assembly, "BlitzProgram", registry.ReferencePaths);

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        start.Environment["BLITZCS_NATIVEINTEROP_LIBRARY"] = libraryPath;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal($"42{Environment.NewLine}3.5{Environment.NewLine}-1{Environment.NewLine}9{Environment.NewLine}native:mundo{Environment.NewLine}1{Environment.NewLine}", await stdout);
        Assert.Equal("", await stderr);
    }

    private static ExternalFunction Function(string name, string method, params Type[] parameters) =>
        new(name, typeof(NativeDemoBindings).GetMethod(method, BindingFlags.Public | BindingFlags.Static, parameters)!);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Add(int left, int right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float Scale(float value, float factor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate sbyte SignedByte(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Utf8Copy(nint input, nuint inputLength, nint output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint MakeMessage(nint input, nuint inputLength, out nuint outputLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint MakeInvalidMessage(out nuint outputLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LiveResourceCount();
}
