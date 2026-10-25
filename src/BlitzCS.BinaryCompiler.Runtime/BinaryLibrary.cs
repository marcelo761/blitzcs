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
/// Process-wide handle to one translated library. All libraries registered by the same owner assembly (one generated binding assembly) form a
/// <see cref="GuestModuleSet"/>: sibling imports are linked by the set, each module gets its own host-environment resolver (see
/// <see cref="IBinaryHostEnvironment"/>), and a module is loaded lazily and thread-safely on the first call together with the siblings it imports.
/// The set is disposed at process exit. Generated BlitzCS bindings use this type; it contains no library-specific code.
/// </summary>
public sealed class BinaryLibrary
{
    private static readonly ConcurrentDictionary<string, BinaryLibrary> Libraries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, LibraryGroup> Groups = new(StringComparer.Ordinal);
    private static readonly Encoding Windows1252 = CreateEncoding();
    private readonly LibraryGroup group;
    private volatile GuestModule? module;

    private BinaryLibrary(string path, LibraryGroup group, BinaryLibraryOptions options)
    {
        Path = path; this.group = group; Options = options;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    internal BinaryLibraryOptions Options { get; }

    static BinaryLibrary()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll(ExitGraceMilliseconds);
    }

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Returns the shared library handle for <paramref name="fileName"/> beside <paramref name="owner"/> (no host environment: imports can only come from siblings).</summary>
    public static BinaryLibrary For(Assembly owner, string fileName) => For(owner, fileName, null);

    /// <summary>Returns the shared library handle for <paramref name="fileName"/> beside <paramref name="owner"/>; the first registration of a file fixes its options.</summary>
    public static BinaryLibrary For(Assembly owner, string fileName, BinaryLibraryOptions? options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var directory = System.IO.Path.GetDirectoryName(owner.Location) ?? AppContext.BaseDirectory;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, fileName));
        var group = Groups.GetOrAdd(directory, static _ => new LibraryGroup());
        return Libraries.GetOrAdd(full, path =>
        {
            var library = new BinaryLibrary(path, group, options ?? new BinaryLibraryOptions());
            group.Register(library);
            return library;
        });
    }

    public BinaryExport Export(string name, GuestCallingConvention convention, int argumentCount) => new(this, name, convention, argumentCount);

    internal GuestModule OpenModule() => module ?? group.Open(this);

    internal void SetModule(GuestModule? loaded) => module = loaded;

    // ---- checked guest memory for BASIC (catalog "intrinsic" functions): pointers/structs for exports that take them ----

    private readonly object memoryGate = new();
    private readonly SortedDictionary<uint, GuestBuffer> allocations = [];
    private const int MaxAllocation = 64 * 1024 * 1024;

    /// <summary>Allocates a zero-filled guest block that stays valid until <see cref="Free"/>; returns its guest address (an Integer for BASIC).</summary>
    public int Alloc(int size)
    {
        if (size is < 1 or > MaxAllocation) throw new BinaryLibraryException($"Binary library '{Name}': allocation size {size} is outside 1..{MaxAllocation}.");
        try
        {
            lock (memoryGate)
            {
                var buffer = OpenModule().Memory.Allocate(size);
                buffer.Write(0, new byte[size]);
                allocations[buffer.Address] = buffer;
                return unchecked((int)buffer.Address);
            }
        }
        catch (Exception error) when (error is GuestException or ObjectDisposedException)
        {
            throw new BinaryLibraryException($"Binary library '{Name}' allocation failed: {error.Message}", error);
        }
    }

    public void Free(int address)
    {
        lock (memoryGate)
        {
            if (!allocations.Remove(unchecked((uint)address), out var buffer))
                throw new BinaryLibraryException($"Binary library '{Name}': 0x{address:X8} is not a live block returned by Alloc.");
            buffer.Dispose();
        }
    }

    private (GuestBuffer Buffer, int Offset) Locate(int address, int length)
    {
        var a = unchecked((uint)address);
        foreach (var (start, buffer) in allocations)
            if (a >= start && (ulong)a + (ulong)length <= (ulong)start + (ulong)buffer.Length) return (buffer, (int)(a - start));
        throw new BinaryLibraryException($"Binary library '{Name}': {length} byte(s) at 0x{a:X8} are outside every live Alloc block.");
    }

    /// <summary>Reads guest memory the library returned a pointer into (any checked, mapped guest range; never writes).</summary>
    public int Peek8(int address) => Peek(address, 1, memory => memory.ReadByte(unchecked((uint)address)));
    public int Peek32(int address) => Peek(address, 4, memory => unchecked((int)memory.ReadUInt32(unchecked((uint)address))));

    private int Peek(int address, int length, Func<GuestMemory, int> read)
    {
        try { lock (memoryGate) return read(OpenModule().Memory); }
        catch (GuestMemoryException error) { throw new BinaryLibraryException($"Binary library '{Name}': {length} byte(s) at 0x{unchecked((uint)address):X8} cannot be read: {error.Message}", error); }
    }
    public void Poke8(int address, int value) { lock (memoryGate) { var (b, o) = Locate(address, 1); b.Write(o, [(byte)value]); } }
    public void Poke32(int address, int value) { lock (memoryGate) { var (b, o) = Locate(address, 4); b.Write(o, BitConverter.GetBytes(value)); } }

    internal static Encoding Ansi => Windows1252;

    /// <summary>
    /// How long process exit waits for a module's in-flight guest calls. A library that started its own worker thread (FMOD's mixer, for one) keeps that thread inside the module
    /// until the program stops it; programs routinely end without doing so, and the operating system then ends the thread with the process.
    /// </summary>
    private const int ExitGraceMilliseconds = 250;

    /// <summary>Disposes every open module, waiting for running calls. Later calls fail with a clear error. (Process exit uses a bounded variant, see <see cref="ExitGraceMilliseconds"/>.)</summary>
    public static void DisposeAll() => DisposeAll(Timeout.Infinite);

    private static void DisposeAll(int graceMilliseconds)
    {
        foreach (var group in Groups.Values) group.Dispose(graceMilliseconds);
    }

    private sealed class LibraryGroup : IDisposable
    {
        private readonly object gate = new();
        private readonly Dictionary<string, BinaryLibrary> members = new(StringComparer.Ordinal);
        private readonly HashSet<string> added = new(StringComparer.Ordinal);
        private GuestModuleSet? set;
        private bool disposed;

        public void Register(BinaryLibrary library)
        {
            lock (gate) members[GuestModuleSet.NormalizeName(library.Name)] = library;
        }

        public GuestModule Open(BinaryLibrary library)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, library);
                if (library.module is { } existing) return existing;
                set ??= new GuestModuleSet(CreateResolver);
                try
                {
                    AddWithDependencies(library);
                    set.LoadAll();
                }
                catch (GuestInitializationException error)
                {
                    throw new BinaryLibraryException($"Binary library '{library.Name}' failed to initialise (DllMain/CRT start-up of it or of a library it imports): {error.Message}", error);
                }
                foreach (var member in members.Values.Where(m => added.Contains(GuestModuleSet.NormalizeName(m.Name))))
                    member.SetModule(set[member.Name]);
                return library.module!;
            }
        }

        private void AddWithDependencies(BinaryLibrary library)
        {
            var key = GuestModuleSet.NormalizeName(library.Name);
            if (added.Contains(key)) return;
            var (artifact, image) = TranslatedLibrary.Read(library.Path);
            set!.Add(library.Name, artifact, image, library.Options.Conventions, new GuestModuleOptions
            {
                RunEntryPoint = library.Options.RunEntryPoint,
                AllowUnboundImports = library.Options.AllowUnboundImports,
            });
            added.Add(key);
            foreach (var import in artifact.Imports.Select(i => GuestModuleSet.NormalizeName(i.Dll)).Distinct())
                if (members.TryGetValue(import, out var sibling) && sibling != library) AddWithDependencies(sibling);
        }

        private IGuestImportResolver CreateResolver(string moduleName)
        {
            var library = members[GuestModuleSet.NormalizeName(moduleName)];
            var options = library.Options;
            if (options.Environment is null) return new DictionaryGuestImportResolver();
            if (!options.EnvironmentRequired)
            {
                var (artifact, _) = TranslatedLibrary.Read(library.Path);
                if (artifact.Imports.All(i => members.ContainsKey(GuestModuleSet.NormalizeName(i.Dll)))) return new DictionaryGuestImportResolver();
            }
            return options.Environment.CreateResolver(library.Name);
        }

        public void Dispose() => Dispose(Timeout.Infinite);

        public void Dispose(int graceMilliseconds)
        {
            lock (gate)
            {
                disposed = true;
                foreach (var member in members.Values) { member.SetModule(null); lock (member.memoryGate) member.allocations.Clear(); }
                try
                {
                    if (graceMilliseconds == Timeout.Infinite) set?.Dispose();
                    else set?.DisposeAtProcessExit(graceMilliseconds);
                }
                catch (Exception error) when (error is GuestException or AggregateException) { }
                set = null;
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
