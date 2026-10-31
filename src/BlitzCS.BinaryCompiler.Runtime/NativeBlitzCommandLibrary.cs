using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Direct Win32 command calls for the fingerprinted Blitz3D runtime. The native module lives until process exit.</summary>
public sealed unsafe partial class NativeBlitzCommandLibrary
{
    public const string SupportedRuntimeSha256 = "ed61cd11cfae77f9f9c47c2ec2e5aa778ff79ddc5ee2f4f7353db4ce6006f39d";
    private static readonly ConcurrentDictionary<string, NativeBlitzCommandLibrary> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path;
    private readonly object initializationGate = new();
    private readonly ConcurrentDictionary<uint, byte> banks = new();
    private nint module;
    private uint imageSize;
    private nint runtimeScratch;
    private bool initialized;
    private Exception? initializationFailure;

    private NativeBlitzCommandLibrary(string path) => this.path = path;

    public static NativeBlitzCommandLibrary For(Assembly owner, string fileName, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(expectedSha256, SupportedRuntimeSha256, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The native Win32 Blitz command backend requires its verified runtime image.");
        var directory = System.IO.Path.GetDirectoryName(owner.Location) ?? AppContext.BaseDirectory;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, fileName));
        return Libraries.GetOrAdd(full, static p => new NativeBlitzCommandLibrary(p));
    }

    public NativeBlitzCommandExport Command(uint rva, string name, int argumentCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (argumentCount is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(argumentCount));
        return new NativeBlitzCommandExport(this, rva, name, argumentCount);
    }

    /// <summary>Adapts a declared userlib pointer without treating arbitrary addresses as bank headers.</summary>
    public NativePointerArgument Pointer(int value)
    {
        if (value < 0 && AppContext.GetData("__BlitzCS.ObjectBuffers") is ConcurrentDictionary<int, (byte[] Bytes, Action<byte[]> WriteBack)> table
            && table.TryRemove(value, out var entry))
            return NativePointerArgument.Copy(entry.Bytes, entry.WriteBack);

        var address = unchecked((uint)value);
        // Only handles returned by this exact native runtime can be dereferenced here.
        // ResizeBank may change the payload address while retaining the bank handle.
        if (banks.ContainsKey(address)) address = *(uint*)(nuint)(address + 4);
        return new NativePointerArgument(unchecked((nint)(nuint)address));
    }

    private nint CodeAddress(uint rva)
    {
        if (module == 0 || rva >= imageSize) throw new BinaryLibraryException($"Native command RVA 0x{rva:X8} is outside the loaded runtime.");
        return module + (nint)rva;
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized)) return;
        lock (initializationGate)
        {
            if (initialized) return;
            if (initializationFailure is not null)
                throw new BinaryLibraryException("Native Blitz runtime initialization previously failed.", initializationFailure);
            try
            {
                var trace = Environment.GetEnvironmentVariable("BLITZCS_NATIVE_INIT_TRACE") == "1";
                void Stage(string stage) { if (trace) Console.Error.WriteLine($"Native Blitz initialization: {stage}"); }
                if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
                    throw new PlatformNotSupportedException("Direct native Blitz commands require a Windows x86 process.");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                if (!string.Equals(hash, SupportedRuntimeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new BinaryLibraryException("The native Blitz runtime file does not match the command bindings' verified image.");
                Stage("loading module");
                module = NativeLibrary.Load(path);
                var nt = Marshal.ReadInt32(module, 0x3C);
                imageSize = unchecked((uint)Marshal.ReadInt32(module, nt + 0x50));
                var baseVa = (uint)(nuint)module;

                // Same MSVC6 startup and string layout used by the translated command backend.
                Stage("runtime startup");
                InvokeThis(0x112C, baseVa + 0xF540C, [baseVa]);
                InvokeStdcallInteger(0x5AD60, [1]);
                InvokeInteger(0xA31BB, [0x0A001F, 0xFFFFF]);
                runtimeScratch = (nint)NativeMemory.AllocZeroed(64);
                if (runtimeScratch == 0) throw new OutOfMemoryException();
                var str = (uint*)(void*)runtimeScratch;
                str[1] = baseVa + 0xD63E4;
                Stage("opening window runtime");
                var runtime = InvokeStdcallInteger(0x3D8F0, [baseVa, (uint)(nuint)runtimeScratch, 0]);
                if (runtime == 0) throw new BinaryLibraryException("Native gxRuntime::openRuntime returned NULL.");
                *(uint*)(nuint)(baseVa + 0xF5410) = runtime;
                *(uint*)(nuint)(baseVa + 0xF554C) = runtime;
                *(byte*)(nuint)(baseVa + 0xF5530) = 0;
                Stage("creating subsystems");
                if ((InvokeInteger(0x4040, []) & 0xFF) == 0)
                    throw new BinaryLibraryException("Native Blitz subsystem initialization returned false.");
                Stage("initial idle");
                InvokeThis(0x3E800, runtime, []);
                Stage("ready");
                Volatile.Write(ref initialized, true);
            }
            catch (Exception error)
            {
                initializationFailure = error;
                // Startup can already have created native worker threads. Retain the module and its scratch state.
                throw;
            }
        }
    }

    internal uint Run(uint rva, string name, int argumentCount, ReadOnlySpan<BinaryArgument> arguments, bool floatingPoint)
    {
        if (arguments.Length != argumentCount)
            throw new ArgumentException($"{name} takes {argumentCount} argument(s), got {arguments.Length}.", nameof(arguments));
        EnsureInitialized();
        // The original executable catches the runtime's end-of-program exception at its entry point.
        // A managed host owns process termination instead of letting that C++ exception cross the CLR boundary.
        if (rva == 0x3810 && name == "End" && argumentCount == 0) Environment.Exit(0);
        Span<uint> words = stackalloc uint[arguments.Length];
        Span<nint> strings = stackalloc nint[arguments.Length];
        var allocated = 0;
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                if (!arguments[i].IsText) { words[i] = arguments[i].Bits; continue; }
                var text = arguments[i].Text!;
                var length = BinaryLibrary.Ansi.GetByteCount(text);
                var copy = (byte*)NativeMemory.Alloc((nuint)checked(length + 1));
                if (copy == null) throw new OutOfMemoryException();
                strings[allocated++] = (nint)copy;
                var bytes = new Span<byte>(copy, length);
                var embeddedNull = text.Contains('\0');
                if (embeddedNull) bytes.Fill((byte)'x');
                else BinaryLibrary.Ansi.GetBytes(text, bytes);
                copy[length] = 0;
                var bbString = InvokeStdcallInteger(0x5900, [(uint)(nuint)copy]);
                if (embeddedNull && bbString != 0)
                {
                    var data = *(uint*)(nuint)(bbString + 4);
                    if (data != 0) BinaryLibrary.Ansi.GetBytes(text, new Span<byte>((void*)(nuint)data, length));
                }
                words[i] = bbString;
            }
            var result = floatingPoint ? BitConverter.SingleToUInt32Bits(InvokeStdcallFloat(rva, words)) : InvokeStdcallInteger(rva, words);
            if (rva == 0x2F890 && name == "CreateBank" && argumentCount == 1 && result != 0)
                banks.TryAdd(result, 0);
            else if (rva == 0x2F970 && name == "FreeBank" && argumentCount == 1)
                banks.TryRemove(words[0], out _);
            return result;
        }
        finally
        {
            for (var i = 0; i < allocated; i++) NativeMemory.Free((void*)strings[i]);
        }
    }

    internal string ReadStringResult(uint address)
    {
        if (address == 0) return "";
        try
        {
            var data = *(uint*)(nuint)(address + 4);
            var size = *(int*)(nuint)(address + 8);
            return size > 0 && data != 0 ? BinaryLibrary.Ansi.GetString(new ReadOnlySpan<byte>((void*)(nuint)data, size)) : "";
        }
        finally { InvokeStdcallInteger(0x5360, [address]); }
    }
}

/// <summary>A command in the verified native Win32 runtime. Numeric arguments retain their 32-bit stack representation.</summary>
public sealed class NativeBlitzCommandExport
{
    private readonly NativeBlitzCommandLibrary library;
    private readonly uint rva;
    private readonly string name;
    private readonly int argumentCount;

    internal NativeBlitzCommandExport(NativeBlitzCommandLibrary library, uint rva, string name, int argumentCount)
    { this.library = library; this.rva = rva; this.name = name; this.argumentCount = argumentCount; }

    public uint Call(params ReadOnlySpan<BinaryArgument> arguments) => library.Run(rva, name, argumentCount, arguments, false);
    public float CallSingle(params ReadOnlySpan<BinaryArgument> arguments) => BitConverter.UInt32BitsToSingle(library.Run(rva, name, argumentCount, arguments, true));
    public string CallString(params ReadOnlySpan<BinaryArgument> arguments) => library.ReadStringResult(library.Run(rva, name, argumentCount, arguments, false));
}
