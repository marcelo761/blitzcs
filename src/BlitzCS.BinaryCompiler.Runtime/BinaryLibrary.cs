using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>A BASIC-visible failure of a call into a translated binary library (the message names library and export).</summary>
public sealed class BinaryLibraryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One argument of a BASIC-to-guest call. Integers and floats are 32-bit stack words; text is copied into a checked guest buffer.</summary>
public readonly struct BinaryArgument
{
    private readonly uint bits;
    private readonly string? text;
    private readonly bool isFloat;
    private BinaryArgument(uint bits, string? text, bool isFloat = false) { this.bits = bits; this.text = text; this.isFloat = isFloat; }

    public static BinaryArgument FromInt32(int value) => new((uint)value, null);
    public static BinaryArgument FromSingle(float value) => new(GuestValue.FromSingle(value), null, true);
    public static BinaryArgument FromString(string? value) => new(0, value ?? "");

    /// <summary>True for BASIC Float arguments: they always travel on the stack, never in ECX/EDX (MSVC fastcall).</summary>
    internal bool IsFloat => isFloat;

    internal bool IsText => text is not null;
    internal uint Bits => bits;
    internal string? Text => text;
}

/// <summary>
/// Process-wide handle to one translated library (single <see cref="GuestModule"/> per file per process).
/// The module is opened lazily and thread-safely on the first call, and disposed at process exit or
/// when the owning assembly load context unloads. Generated BlitzCS bindings use this type; it contains no library-specific code.
/// </summary>
public sealed class BinaryLibrary
{
    private static readonly ConcurrentDictionary<string, BinaryLibrary> Libraries = new(StringComparer.Ordinal);
    private static readonly Encoding Windows1252 = CreateEncoding();
    private readonly object gate = new();
    private GuestModule? module;
    private bool disposed;

    private BinaryLibrary(string path) { Path = path; }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);

    static BinaryLibrary()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Returns the shared library handle for <paramref name="fileName"/> beside <paramref name="owner"/>.</summary>
    public static BinaryLibrary For(Assembly owner, string fileName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var directory = System.IO.Path.GetDirectoryName(owner.Location) ?? AppContext.BaseDirectory;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, fileName));
        return Libraries.GetOrAdd(full, static path => new BinaryLibrary(path));
    }

    public BinaryExport Export(string name, GuestCallingConvention convention, int argumentCount) => new(this, name, convention, argumentCount);

    internal GuestModule OpenModule()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return module ??= TranslatedLibrary.Open(Path, new DictionaryGuestImportResolver());
        }
    }

    internal static Encoding Ansi => Windows1252;

    /// <summary>Disposes every open module (also runs at process exit). Later calls fail with a clear error.</summary>
    public static void DisposeAll()
    {
        foreach (var library in Libraries.Values)
        {
            lock (library.gate)
            {
                library.disposed = true;
                try { library.module?.Dispose(); } catch (GuestException) { }
                library.module = null;
            }
        }
    }
}

/// <summary>An export of a <see cref="BinaryLibrary"/>, resolved once on first use.</summary>
public sealed class BinaryExport
{
    private readonly BinaryLibrary library;
    private readonly string name;
    private readonly GuestCallingConvention convention;
    private readonly int argumentCount;
    private GuestFunction? function;
    private readonly object gate = new();

    internal BinaryExport(BinaryLibrary library, string name, GuestCallingConvention convention, int argumentCount)
    {
        this.library = library; this.name = name; this.convention = convention; this.argumentCount = argumentCount;
    }

    /// <summary>Calls the export and returns EAX. Text arguments are NUL-terminated Windows-1252 copies freed after the call.</summary>
    public uint Call(params ReadOnlySpan<BinaryArgument> arguments) => Run(GuestReturn.Eax, arguments).Eax;

    /// <summary>
    /// Calls an export that returns a float: the callee leaves it in ST0 (cdecl, stdcall, thiscall and fastcall alike). The 80-bit register is narrowed once,
    /// to nearest even, to single precision, which is what a caller of a float-returning function observes. A callee that leaves ST0 empty
    /// (it actually returns an integer or void) is an error, not a zero.
    /// </summary>
    public float CallSingle(params ReadOnlySpan<BinaryArgument> arguments) => Run(GuestReturn.St0, arguments).St0Single;

    private GuestCallResult Run(GuestReturn kind, ReadOnlySpan<BinaryArgument> arguments)
    {
        List<GuestBuffer>? buffers = null;
        try
        {
            var bound = Resolve();
            var module = library.OpenModule();
            var words = new uint[arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                if (!arguments[i].IsText) { words[i] = arguments[i].Bits; continue; }
                var bytes = BinaryLibrary.Ansi.GetBytes(arguments[i].Text!);
                var buffer = module.Memory.Allocate(bytes.Length + 1);
                (buffers ??= []).Add(buffer);
                buffer.Write(0, bytes);
                words[i] = buffer.Address;
            }
            switch (bound.Convention)
            {
                case GuestCallingConvention.Thiscall:
                    // Catalog rule: the first declared parameter is 'this' (ECX); the others are stack words in declaration order.
                    return bound.InvokeRegisters(kind, Math.Min(1, words.Length), words.Length > 0 ? words[0] : 0u, 0u, words.AsSpan(Math.Min(1, words.Length)));
                case GuestCallingConvention.Fastcall:
                {
                    // MSVC rule: the first two integer-class (Integer, String pointer) arguments take ECX, EDX in order; Floats and the rest stay on the stack.
                    Span<int> reg = stackalloc int[2];
                    var n = 0;
                    for (var i = 0; i < arguments.Length && n < 2; i++)
                        if (!arguments[i].IsFloat) reg[n++] = i;
                    var stack = new uint[words.Length - n];
                    for (int i = 0, k = 0; i < words.Length; i++)
                        if (!(n > 0 && i == reg[0]) && !(n > 1 && i == reg[1])) stack[k++] = words[i];
                    return bound.InvokeRegisters(kind, n, n > 0 ? words[reg[0]] : 0u, n > 1 ? words[reg[1]] : 0u, stack);
                }
                default:
                    return bound.Invoke(kind, words);
            }
        }
        catch (Exception error) when (error is GuestException or TranslatedLibraryException or ArgumentException
            or NotSupportedException or PlatformNotSupportedException or ObjectDisposedException or IOException)
        {
            throw new BinaryLibraryException($"Binary library '{library.Name}' export '{name}' failed: {error.Message}", error);
        }
        finally
        {
            if (buffers is not null)
                foreach (var buffer in buffers)
                    try { buffer.Dispose(); } catch (Exception error) when (error is GuestException or ObjectDisposedException) { }
        }
    }

    private GuestFunction Resolve()
    {
        lock (gate)
            return function ??= library.OpenModule().Bind(name, convention, argumentCount);
    }
}
