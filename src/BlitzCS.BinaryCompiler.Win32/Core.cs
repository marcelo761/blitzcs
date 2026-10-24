using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Calling convention of an emulated import. Win32 APIs are stdcall (callee pops 4*argc); the C runtime is cdecl.</summary>
public enum Win32Convention { Stdcall, Cdecl }

/// <summary>Win32 error codes (winerror.h values) used by the services.</summary>
public static class Win32Error
{
    public const uint Success = 0, InvalidFunction = 1, FileNotFound = 2, PathNotFound = 3, AccessDenied = 5, InvalidHandle = 6,
        NotEnoughMemory = 8, InvalidData = 13, OutOfMemory = 14, NoMoreFiles = 18, NotSupported = 50, FileExists = 80, InvalidParameter = 87,
        InsufficientBuffer = 122, ModNotFound = 126, ProcNotFound = 127, NegativeSeek = 131, BadPathname = 161, AlreadyExists = 183,
        EnvVarNotFound = 203, InvalidAddress = 487, NoMoreItems = 259, InvalidFlags = 1004, NoUnicodeTranslation = 1113, HandleEof = 38,
        NotOwner = 288, TooManyPosts = 298, InvalidUserBuffer = 1784, NotEnoughQuota = 1816;
}

/// <summary>Raised when the guest calls the emulated Win32 surface in a way real Windows would answer with a crash or undefined behaviour (for example leaving an unowned critical section). The guest call is abandoned; nothing is silently ignored.</summary>
public sealed class Win32UsageException(string message) : Exception(message);

/// <summary>ExitProcess/TerminateProcess(self). The host process is never terminated: the guest call is abandoned with this exception (it is the inner exception of the runtime's GuestImportException).</summary>
public sealed class Win32ExitProcessException(uint exitCode, string api) : Exception($"The guest called {api}({exitCode}); the host process keeps running.")
{
    public uint ExitCode { get; } = exitCode;
}

/// <summary>An allocation of guest-visible memory, owned by the service that requested it.</summary>
public interface IWin32Allocation : IDisposable
{
    uint Address { get; }
    int Length { get; }
}

/// <summary>Everything the services need from guest memory. Guest pointers are plain 32-bit values; no host pointer is ever exposed.</summary>
public interface IWin32Memory
{
    byte ReadByte(uint address);
    uint ReadUInt32(uint address);
    void WriteByte(uint address, byte value);
    void WriteUInt32(uint address, uint value);
    void Read(uint address, Span<byte> destination);
    void Write(uint address, ReadOnlySpan<byte> source);
    string ReadAnsiString(uint address, int maxLength = 65536);
    bool IsMapped(uint address, uint length);
    /// <summary>Allocates a zeroed, page-granular guest region. Throws on exhaustion.</summary>
    IWin32Allocation Allocate(int size);
}

/// <summary>Adapts the runtime's <see cref="GuestMemory"/>.</summary>
public sealed class GuestMemoryAdapter(GuestMemory memory) : IWin32Memory
{
    private sealed class Alloc(GuestBuffer buffer) : IWin32Allocation
    {
        public uint Address => buffer.Address;
        public int Length => buffer.Length;
        public void Dispose() => buffer.Dispose();
    }

    public GuestMemory Memory { get; } = memory;
    public byte ReadByte(uint a) => Memory.ReadByte(a);
    public uint ReadUInt32(uint a) => Memory.ReadUInt32(a);
    public void WriteByte(uint a, byte v) => Memory.WriteByte(a, v);
    public void WriteUInt32(uint a, uint v) => Memory.WriteUInt32(a, v);
    public void Read(uint a, Span<byte> d) => Memory.Read(a, d);
    public void Write(uint a, ReadOnlySpan<byte> s) => Memory.Write(a, s);
    public string ReadAnsiString(uint a, int max = 65536) => Memory.ReadAnsiString(a, max);
    public bool IsMapped(uint a, uint l) => Memory.IsMapped(a, l);
    public IWin32Allocation Allocate(int size) => new Alloc(Memory.Allocate(size));
}

public static class Win32MemoryExtensions
{
    public static ushort ReadUInt16(this IWin32Memory m, uint a) { Span<byte> b = stackalloc byte[2]; m.Read(a, b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    public static void WriteUInt16(this IWin32Memory m, uint a, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); m.Write(a, b); }
    public static void WriteUInt64(this IWin32Memory m, uint a, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); m.Write(a, b); }
    public static byte[] ReadBytes(this IWin32Memory m, uint a, int length) { var b = new byte[length]; m.Read(a, b); return b; }

    public static void Fill(this IWin32Memory m, uint address, byte value, uint length)
    {
        var chunk = new byte[(int)Math.Min(length, 65536u)];
        if (value != 0) Array.Fill(chunk, value);
        for (uint done = 0; done < length;)
        {
            var n = (int)Math.Min(chunk.Length, length - done);
            m.Write(address + done, chunk.AsSpan(0, n));
            done += (uint)n;
        }
    }

    /// <summary>memmove semantics (overlap-safe), chunked through a host buffer.</summary>
    public static void Copy(this IWin32Memory m, uint destination, uint source, uint length)
    {
        if (length == 0 || destination == source) return;
        var chunk = new byte[(int)Math.Min(length, 65536u)];
        if (destination < source || destination >= source + length)
            for (uint done = 0; done < length;)
            {
                var n = (int)Math.Min(chunk.Length, length - done);
                m.Read(source + done, chunk.AsSpan(0, n)); m.Write(destination + done, chunk.AsSpan(0, n));
                done += (uint)n;
            }
        else
            for (var left = length; left > 0;)
            {
                var n = (int)Math.Min(chunk.Length, left);
                left -= (uint)n;
                m.Read(source + left, chunk.AsSpan(0, n)); m.Write(destination + left, chunk.AsSpan(0, n));
            }
    }

    /// <summary>Length of the NUL-terminated byte string at <paramref name="a"/> (reads page-safe chunks).</summary>
    public static int StrLen(this IWin32Memory m, uint a, int max = 1 << 24)
    {
        for (var i = 0; i < max; i++) if (m.ReadByte(a + (uint)i) == 0) return i;
        throw new Win32UsageException($"Unterminated string at 0x{a:X8}.");
    }

    public static byte[] ReadCString(this IWin32Memory m, uint a) => m.ReadBytes(a, m.StrLen(a));

    public static string ReadWideString(this IWin32Memory m, uint a, int maxChars = 1 << 20)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < maxChars; i++)
        {
            var c = m.ReadUInt16(a + 2u * (uint)i);
            if (c == 0) return sb.ToString();
            sb.Append((char)c);
        }
        throw new Win32UsageException($"Unterminated wide string at 0x{a:X8}.");
    }

    public static void WriteCString(this IWin32Memory m, uint a, string latin1)
    {
        var b = new byte[latin1.Length + 1];
        for (var i = 0; i < latin1.Length; i++) b[i] = (byte)latin1[i];
        m.Write(a, b);
    }
}

/// <summary>One decoded import call. <see cref="Args"/> holds exactly the declared argument count; <see cref="VarArg"/> reads further stack words of cdecl varargs APIs.</summary>
public readonly ref struct Win32Call
{
    internal Win32Call(Win32Context ctx, IWin32Memory mem, ReadOnlySpan<uint> args, Func<int, uint>? varArg)
    { Ctx = ctx; Mem = mem; Args = args; varReader = varArg; }

    private readonly Func<int, uint>? varReader;
    public Win32Context Ctx { get; }
    public IWin32Memory Mem { get; }
    public ReadOnlySpan<uint> Args { get; }
    public uint this[int index] => Args[index];
    public uint VarArg(int index) => index < Args.Length ? Args[index] : varReader is { } r ? r(index) : throw new Win32UsageException("This API does not take variable arguments.");
}

public delegate uint Win32Handler(Win32Call call);

/// <summary>One implemented import: its identity, calling convention and exact argument count.</summary>
public sealed class Win32Api
{
    public required string Dll { get; init; }
    public required string Name { get; init; }
    public ushort? Ordinal { get; init; }
    public required Win32Convention Convention { get; init; }
    public required int ArgCount { get; init; }
    public bool Variadic { get; init; }
    public required string Service { get; init; }
    public required Win32Handler Handler { get; init; }
    public override string ToString() => $"{Dll}!{Name}";
}

/// <summary>The table of implemented imports. Anything absent here is, by construction, reported as missing.</summary>
public sealed class Win32Registry
{
    private readonly Dictionary<string, Win32Api> map = new(StringComparer.Ordinal);
    private readonly List<Win32Api> all = [];
    public IReadOnlyList<Win32Api> All => all;

    public static string NormalizeDll(string dll)
    {
        var d = dll.ToLowerInvariant();
        return Path.HasExtension(d) ? d : d + ".dll";
    }

    internal static string Key(string dll, string name) => NormalizeDll(dll) + "!" + name;

    public void Add(Win32Api api)
    {
        if (!map.TryAdd(Key(api.Dll, api.Name), api)) throw new InvalidOperationException($"{api} registered twice.");
        if (api.Ordinal is { } o) map.Add(Key(api.Dll, "#" + o), api);
        all.Add(api);
    }

    public Win32Api? Find(string dll, string? name, ushort? ordinal) =>
        map.TryGetValue(Key(dll, name ?? "#" + ordinal), out var api) ? api : null;
}

/// <summary>An independent group of related APIs. A service owns no global state: its state lives in <see cref="Win32Context"/>.</summary>
public interface IWin32Service
{
    string Name { get; }
    void Register(Win32Registry registry);
}

/// <summary>Registration helpers shared by the services.</summary>
public abstract class Win32Service : IWin32Service
{
    private Win32Registry? registry;
    public abstract string Name { get; }
    protected abstract void Define();

    public void Register(Win32Registry r) { registry = r; Define(); registry = null; }

    protected void Std(string dll, string name, int argc, Win32Handler h) => Add(dll, name, Win32Convention.Stdcall, argc, false, null, h);
    protected void StdOrdinal(string dll, string name, ushort ordinal, int argc, Win32Handler h) => Add(dll, name, Win32Convention.Stdcall, argc, false, ordinal, h);
    protected void Cdecl(string dll, string name, int argc, Win32Handler h) => Add(dll, name, Win32Convention.Cdecl, argc, false, null, h);
    protected void CdeclVar(string dll, string name, int fixedArgc, Win32Handler h) => Add(dll, name, Win32Convention.Cdecl, fixedArgc, true, null, h);

    private void Add(string dll, string name, Win32Convention conv, int argc, bool variadic, ushort? ordinal, Win32Handler h) =>
        registry!.Add(new Win32Api { Dll = dll, Name = name, Ordinal = ordinal, Convention = conv, ArgCount = argc, Variadic = variadic, Service = Name, Handler = h });
}

public sealed record Win32Version(uint Major = 5, uint Minor = 1, uint Build = 2600, string Csd = "Service Pack 3", ushort ServicePackMajor = 3);

/// <summary>Host policy of the emulated process. Nothing here defaults to leaking the host (environment, file system).</summary>
public sealed class Win32Options
{
    /// <summary>Directory that "C:\" and relative Windows paths map into. Null: every path-based file API fails with ERROR_ACCESS_DENIED.</summary>
    public string? SandboxRoot { get; init; }
    public bool CaseInsensitiveFileNames { get; init; } = true;
    public string CommandLine { get; init; } = "program.exe";
    public string MainModulePath { get; init; } = "C:\\program.exe";
    public uint MainModuleBase { get; init; } = 0x00400000;
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public int AnsiCodePage { get; init; } = 1252;
    public int OemCodePage { get; init; } = 437;
    public Win32Version Version { get; init; } = new();
    public Stream? Stdin { get; init; }
    public Stream? Stdout { get; init; }
    public Stream? Stderr { get; init; }
    /// <summary>Upper bound for a single VirtualAlloc/HeapAlloc request (the runtime caps one guest region at 256 MiB).</summary>
    public long MaxAllocation { get; init; } = 256L * 1024 * 1024;
}

/// <summary>Per-process emulation state shared by the services. One instance must serve exactly one guest module.</summary>
public sealed class Win32Context : IDisposable
{
    private readonly object bindGate = new();
    private IWin32Memory? memory;
    private object? memoryOwner;
    private readonly ThreadLocal<uint> lastError = new(() => 0);
    private uint nextHandle = 0x100;
    private readonly ConcurrentDictionary<uint, object> handles = new();

    public Win32Context(Win32Options options)
    {
        Options = options;
        Heaps = new HeapState(this);
        Virtual = new VirtualState();
        Sync = new SyncState();
        Tls = new TlsState();
        Process = new ProcessState(this);
        Files = new FileState(this);
    }

    public Win32Options Options { get; }
    internal HeapState Heaps { get; }
    internal VirtualState Virtual { get; }
    internal SyncState Sync { get; }
    internal TlsState Tls { get; }
    internal ProcessState Process { get; }
    internal FileState Files { get; }

    public uint LastError { get => lastError.Value; set => lastError.Value = value; }

    /// <summary>Sets the last error and returns <paramref name="result"/> (FALSE/NULL by default).</summary>
    public uint Fail(uint error, uint result = 0) { LastError = error; return result; }

    /// <summary>Makes a guest or host module visible to GetModuleHandleA/LoadLibraryA/GetProcAddress. Exports map names (or "#ordinal") to guest addresses.</summary>
    public Win32Module RegisterModule(string name, uint handle, string path, IReadOnlyDictionary<string, uint>? exports = null) => Process.Register(name, handle, path, exports);

    /// <summary>Runtime hook (not available yet): returns a guest-callable address for an emulated export so GetProcAddress can hand it out.</summary>
    public void SetExportStubAddressProvider(Func<string, string, uint?>? provider) => Process.ExportStubAddress = provider;

    public uint CurrentThreadId => 1000u + 4u * (uint)Environment.CurrentManagedThreadId;

    /// <summary>Binds the context to the memory of the first guest module that calls it; another module is refused.</summary>
    internal IWin32Memory Bind(GuestMemory guest)
    {
        lock (bindGate)
        {
            if (memory is null) { memoryOwner = guest; memory = new GuestMemoryAdapter(guest); }
            else if (!ReferenceEquals(memoryOwner, guest)) throw new Win32UsageException("This Win32ImportResolver already serves another guest module; create one resolver per module.");
            return memory;
        }
    }

    internal void BindForHost(IWin32Memory mem)
    {
        lock (bindGate)
        {
            if (memory is null) { memory = mem; memoryOwner = mem; }
            else if (!ReferenceEquals(memoryOwner, mem)) throw new Win32UsageException("This context is already bound to another memory.");
        }
    }

    // ---- handle table: monotonically increasing, multiples of 4; a closed handle is never reissued, so double close is detectable ----

    internal uint NewHandle(object value)
    {
        var h = Interlocked.Add(ref nextHandle, 4);
        handles[h] = value;
        return h;
    }

    internal void SetHandle(uint handle, object value) => handles[handle] = value;
    internal T? GetHandle<T>(uint handle) where T : class => handles.TryGetValue(handle, out var v) ? v as T : null;
    internal bool RemoveHandle(uint handle, out object? value) => handles.TryRemove(handle, out value);

    public void Dispose()
    {
        Heaps.Dispose();
        Virtual.Dispose();
        Files.Dispose();
        Process.Dispose();
        foreach (var v in handles.Values) (v as IDisposable)?.Dispose();
        lastError.Dispose();
    }
}
