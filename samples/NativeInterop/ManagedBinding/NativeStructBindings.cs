using System.Runtime.InteropServices;
using BlitzCS.NativeInterop;

namespace BlitzCS.NativeInterop.ManagedBinding;

/// <summary>BASIC struct-pointer facade over the fixture's <c>blitz_status_update(BlitzStatus*)</c>.</summary>
/// <remarks>The BASIC Type supplies eight 4-byte slots: length, input, scale#, big, small, wide (low, high)
/// and output. The layout translates them to the C struct's size_t and uint64_t fields.</remarks>
public static class NativeStructBindings
{
    public static readonly NativeStructLayout Status = NativeStructLayout.Sequential(
        NativeStructField.UInt32, NativeStructField.Int32, NativeStructField.Float32,
        NativeStructField.UIntPtrSaturating, NativeStructField.UIntPtrChecked,
        NativeStructField.UInt64, NativeStructField.Int32);

    private static readonly Lazy<(NativeLibraryOwner Library, NativeFunction<UpdateFunction> Update)> Api =
        new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int StatusIn(ReadOnlySpan<byte> status) => Status.Invoke(status, Update);

    public static int StatusOut([Out] Span<byte> status) => Status.Invoke(status, NativeStructDirection.Out, Update);

    public static int StatusInOut(Span<byte> status) => Status.Invoke(status, NativeStructDirection.InOut, Update);

    private static int Update(nint pointer) => Api.Value.Update.Invoke(call => call(pointer));

    private static (NativeLibraryOwner, NativeFunction<UpdateFunction>) Create()
    {
        var path = Environment.GetEnvironmentVariable("BLITZCS_NATIVEINTEROP_BUFFER_LIBRARY");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "blitzcs_native_buffer.dll" : "libblitzcs_native_buffer.so");
        var library = NativeLibraryOwner.Load(path);
        try { return (library, library.Bind<UpdateFunction>("blitz_status_update")); }
        catch
        {
            library.Dispose();
            throw;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UpdateFunction(nint status);
}
