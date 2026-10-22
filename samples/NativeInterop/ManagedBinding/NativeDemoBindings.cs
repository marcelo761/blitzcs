using System.Runtime.InteropServices;
using System.Text;
using BlitzCS.NativeInterop;

namespace BlitzCS.NativeInterop.ManagedBinding;

/// <summary>Primitive-only BASIC facade over the real native demo library.</summary>
public static class NativeDemoBindings
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Lazy<Bindings> Api = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Add(int left, int right) => Api.Value.Add.Invoke(call => call(left, right));

    public static float Scale(float value, float factor) => Api.Value.Scale.Invoke(call => call(value, factor));

    /// <summary>Widens the native signed 8-bit result to the BASIC integer type.</summary>
    public static int SignedByte(int value) => Api.Value.SignedByte.Invoke(call => (int)call(value));

    /// <summary>Disposes the cached native library; call after the final native-backed BASIC function.</summary>
    public static int Close()
    {
        if (Api.IsValueCreated) Api.Value.Library.Dispose();
        return 1;
    }

    /// <summary>Copies UTF-8 through a buffer allocated and owned by managed code, returning its byte length.</summary>
    public static int Utf8ByteLength(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var input = StrictUtf8.GetBytes(value);
        nint inputPointer = 0;
        nint outputPointer = 0;
        try
        {
            inputPointer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
            outputPointer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
            if (input.Length != 0) Marshal.Copy(input, 0, inputPointer, input.Length);
            return checked((int)Api.Value.Copy.Invoke(call => call(inputPointer, (nuint)input.Length, outputPointer, (nuint)input.Length)));
        }
        finally
        {
            Marshal.FreeHGlobal(outputPointer);
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    /// <summary>Reads a native-allocated UTF-8 result and releases it through the matching native free export.</summary>
    public static string Message(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var input = StrictUtf8.GetBytes(value);
        var inputPointer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        try
        {
            if (input.Length != 0) Marshal.Copy(input, 0, inputPointer, input.Length);
            using var resource = Api.Value.Make.Invoke(call =>
            {
                var pointer = call(inputPointer, (nuint)input.Length, out var length);
                return NativeUtf8Resource.Own(Api.Value.Library, Api.Value.Free, pointer, length);
            });
            return resource.Read();
        }
        finally { Marshal.FreeHGlobal(inputPointer); }
    }

    private static Bindings Create()
    {
        var path = Environment.GetEnvironmentVariable("BLITZCS_NATIVEINTEROP_LIBRARY");
        if (string.IsNullOrWhiteSpace(path))
        {
            var filename = OperatingSystem.IsWindows() ? "blitzcs_native_demo.dll"
                : OperatingSystem.IsLinux() ? "libblitzcs_native_demo.so"
                : throw new PlatformNotSupportedException("The native interop sample currently supports Linux and Windows libraries.");
            path = Path.Combine(AppContext.BaseDirectory, filename);
        }
        var library = NativeLibraryOwner.Load(path);
        try
        {
            return new Bindings(library,
                library.Bind<AddFunction>("blitz_add"),
                library.Bind<ScaleFunction>("blitz_scale"),
                library.Bind<SignedByteFunction>("blitz_signed_byte"),
                library.Bind<CopyFunction>("blitz_utf8_copy"),
                library.Bind<MakeFunction>("blitz_make_message"),
                library.Bind<MakeInvalidFunction>("blitz_make_invalid_message"),
                library.Bind<LiveResourceCountFunction>("blitz_live_resource_count"),
                library.Bind<NativeUtf8Resource.FreeResource>("blitz_free"));
        }
        catch
        {
            library.Dispose();
            throw;
        }
    }

    private sealed record Bindings(NativeLibraryOwner Library, NativeFunction<AddFunction> Add,
        NativeFunction<ScaleFunction> Scale, NativeFunction<SignedByteFunction> SignedByte,
        NativeFunction<CopyFunction> Copy, NativeFunction<MakeFunction> Make,
        NativeFunction<MakeInvalidFunction> MakeInvalid, NativeFunction<LiveResourceCountFunction> LiveResourceCount,
        NativeFunction<NativeUtf8Resource.FreeResource> Free);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddFunction(int left, int right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float ScaleFunction(float value, float factor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate sbyte SignedByteFunction(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint CopyFunction(nint input, nuint inputLength, nint output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint MakeFunction(nint input, nuint inputLength, out nuint outputLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint MakeInvalidFunction(out nuint outputLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LiveResourceCountFunction();
}
