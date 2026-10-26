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
        NotOwner = 288, TooManyPosts = 298, InvalidUserBuffer = 1784, NotEnoughQuota = 1816, BadLength = 24, NoAccess = 998, BadExeFormat = 193, DllInitFailed = 1114,
        Timeout = 1460, ResourceDataNotFound = 1812, ResourceTypeNotFound = 1813, ResourceNameNotFound = 1814, ResourceLangNotFound = 1815;
}

/// <summary>Raised when the guest calls the emulated Win32 surface in a way real Windows would answer with a crash or undefined behaviour (for example leaving an unowned critical section). The guest call is abandoned; nothing is silently ignored.</summary>
public class Win32UsageException(string message) : Exception(message);

/// <summary>
/// The guest asked to make translated code writable (VirtualProtect with a writable protection on a page of an executable section), which means it intends to patch its own code.
/// Ahead-of-time translation cannot follow self-modifying code: succeeding would let the guest write bytes that the translated code never reads. The request is therefore refused by name.
/// </summary>
public sealed class Win32SelfModifyingCodeException(string module, uint start, uint end, uint requestedProtection)
    : Win32UsageException($"The guest asked to make translated code writable (self-modifying code), which translation cannot honour: VirtualProtect on {module}, range 0x{start:X8}-0x{end:X8}, requested protection 0x{requestedProtection:X}.")
{
    public string Module { get; } = module;
    public uint Start { get; } = start;
    public uint End { get; } = end;
    public uint RequestedProtection { get; } = requestedProtection;
}

/// <summary>ExitProcess/TerminateProcess(self). The host process is never terminated: the guest call is abandoned with this exception (it is the inner exception of the runtime's GuestImportException).</summary>
public sealed class Win32ExitProcessException(uint exitCode, string api) : Exception($"The guest called {api}({exitCode}); the host process keeps running."), IGuestProcessExit
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

    /// <summary>Describes the page containing <paramref name="address"/>. The default (memory without page protection support) reports nothing mapped.</summary>
    bool TryQueryPage(uint address, out GuestPageInfo info) { info = new GuestPageInfo(GuestPageKind.Unmapped, 0, 0, false, false, 0, null); return false; }
    /// <summary>Lowest mapped address above <paramref name="address"/>, or 0x100000000.</summary>
    ulong NextMappedAddress(uint address) => 0x1_0000_0000UL;
    /// <summary>Changes the host protection of whole pages and records <paramref name="tag"/> (see <see cref="GuestMemory.TryProtect"/>). The default refuses.</summary>
    bool TryProtect(uint address, uint length, MemoryProtection protection, uint tag, out string? refusal) { refusal = "this memory does not support protection changes"; return false; }
    /// <summary>Records that the guest asked for these pages to be executable (or no longer executable): where code generated at run time may be translated. The default ignores it.</summary>
    void SetGuestExecutable(uint address, uint length, bool executable) { }
    /// <summary>Drops translations of code generated at run time in the range (decommit). The default has none.</summary>
    int InvalidateDynamicCode(uint address, uint length) => 0;
}

/// <summary>Adapts the runtime's <see cref="GuestMemory"/>.</summary>
public sealed class GuestMemoryAdapter(GuestMemory memory, GuestMemory? allocator = null) : IWin32Memory
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
    /// <summary>Allocations come from <paramref name="allocator"/> when given (the process anchor of a shared Win32 state), else from this memory's own module.</summary>
    public IWin32Allocation Allocate(int size) => new Alloc((allocator ?? Memory).Allocate(size));
    public bool TryQueryPage(uint a, out GuestPageInfo info) => Memory.TryQueryPage(a, out info);
    public ulong NextMappedAddress(uint a) => Memory.NextMappedAddress(a);
    public bool TryProtect(uint a, uint l, MemoryProtection p, uint tag, out string? refusal) => Memory.TryProtect(a, l, p, tag, out refusal);
    public void SetGuestExecutable(uint a, uint l, bool executable) => Memory.SetGuestExecutable(a, l, executable);
    public int InvalidateDynamicCode(uint a, uint l) => Memory.InvalidateDynamicCode(a, l);
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
    internal Win32Call(Win32Context ctx, IWin32Memory mem, ReadOnlySpan<uint> args, Func<int, uint>? varArg, GuestCall? guest = null)
    { Ctx = ctx; Mem = mem; Args = args; varReader = varArg; this.guest = guest; }

    private readonly GuestCall? guest;

    /// <summary>The guest call behind this API call (for APIs implemented by the guest runtime itself, such as SEH); throws when the API runs for host code.</summary>
    internal GuestCall Guest => guest ?? throw new Win32UsageException("This API needs a running guest module.");

    /// <summary>Calls guest code at <paramref name="address"/> (callbacks such as _initterm entries, qsort comparers). Only available when the API runs for a guest module.</summary>
    public uint CallGuest(uint address, params ReadOnlySpan<uint> args) =>
        (guest ?? throw new Win32UsageException("This API calls back into guest code and needs a running guest module.")).CallGuest(address, args);

    /// <summary>A capturable form of <see cref="CallGuest"/> (valid on the same thread, inside the handler) for local functions.</summary>
    public Func<uint, uint[], uint> GuestCaller()
    {
        var g = guest ?? throw new Win32UsageException("This API calls back into guest code and needs a running guest module.");
        return (address, args) => g.CallGuest(address, args);
    }

    /// <summary>A capturable reader of the variable arguments (index counts from the first declared argument).</summary>
    public Func<int, uint> VarArgs()
    {
        var all = Args.ToArray(); var reader = varReader;
        return i => i < all.Length ? all[i] : reader is { } r ? r(i) : throw new Win32UsageException("This API does not take variable arguments.");
    }

    /// <summary>The x87 argument of an x87-convention import (ST0), popped. Only available for a guest module.</summary>
    public double PopSt0Double() => (guest ?? throw new Win32UsageException("x87 arguments need a running guest module.")).PopSt0Double();

    /// <summary>Also returns <paramref name="value"/> in EDX (high half of a 64-bit result).</summary>
    public void ReturnEdx(uint value) => (guest ?? throw new Win32UsageException("EDX results need a running guest module.")).SetReturnEdx(value);

    /// <summary>
    /// Sets the high half of a 64-bit result (EDX for a guest; always also recorded in <see cref="Win32Context.HighResult"/> so host-side callers and tests can read it).
    /// The low half is the handler's return value.
    /// </summary>
    public void ReturnHigh(uint value) { Ctx.HighResult = value; guest?.SetReturnEdx(value); }

    /// <summary>Returns a double in ST0.</summary>
    public void ReturnSt0(double value) => (guest ?? throw new Win32UsageException("x87 results need a running guest module.")).SetReturnSt0(value);

    /// <summary>Reads the double that occupies argument words <paramref name="index"/> and <paramref name="index"/>+1 (cdecl/stdcall pass a double as two words, low first).</summary>
    public double Double(int index) => BitConverter.UInt64BitsToDouble(this[index] | (ulong)this[index + 1] << 32);

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
        if (d is "crtdll" or "crtdll.dll") return "msvcrt.dll";
        return Path.HasExtension(d) ? d : d + ".dll";
    }

    internal static string Key(string dll, string name) => NormalizeDll(dll) + "!" + name;

    public void Add(Win32Api api)
    {
        if (!map.TryAdd(Key(api.Dll, api.Name), api)) throw new InvalidOperationException($"{api} registered twice.");
        if (api.Ordinal is { } o) map.Add(Key(api.Dll, "#" + o), api);
        all.Add(api);
    }

    private readonly Dictionary<string, Func<Win32Context, IWin32Memory, uint>> data = new(StringComparer.Ordinal);

    /// <summary>Registers an exported variable. The factory returns its guest address (allocated once per module).</summary>
    public void AddData(string dll, string name, Func<Win32Context, IWin32Memory, uint> factory)
    {
        if (!data.TryAdd(Key(dll, name), factory)) throw new InvalidOperationException($"{dll}!{name} (data) registered twice.");
    }

    public Func<Win32Context, IWin32Memory, uint>? FindData(string dll, string? name) =>
        name is not null && data.TryGetValue(Key(dll, name), out var f) ? f : null;

    public IEnumerable<string> DataNames => data.Keys;

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

    protected void Data(string dll, string name, Func<Win32Context, IWin32Memory, uint> factory) => registry!.AddData(dll, name, factory);
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
    /// <summary>Loads guest DLLs for LoadLibrary* / GetModuleHandle* / GetProcAddress / FreeLibrary (null: only the emulated DLLs and host-registered modules are loadable). See <see cref="GuestLibraryLoader"/>.</summary>
    public GuestLibraryLoader? GuestLibraries { get; init; }
    public bool CaseInsensitiveFileNames { get; init; } = true;
    public string CommandLine { get; init; } = "program.exe";
    public string MainModulePath { get; init; } = "C:\\program.exe";
    public uint MainModuleBase { get; init; } = 0x00400000;
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>Time zone the guest sees (GetTimeZoneInformation, GetLocalTime). Default: the host's local zone.</summary>
    public TimeZoneInfo? TimeZone { get; init; }
    public int AnsiCodePage { get; init; } = 1252;
    public int OemCodePage { get; init; } = 437;
    public Win32Version Version { get; init; } = new();
    /// <summary>True: the standard handles are console handles (GetConsoleMode succeeds, WriteConsole/ReadConsole work on them). False (default): redirected streams, where the console APIs fail with ERROR_INVALID_HANDLE exactly as on Windows.</summary>
    public bool StdoutIsConsole { get; init; }
    public bool StderrIsConsole { get; init; }
    public bool StdinIsConsole { get; init; }
    public Stream? Stdin { get; init; }
    public Stream? Stdout { get; init; }
    public Stream? Stderr { get; init; }
    /// <summary>Upper bound for a single VirtualAlloc/HeapAlloc request (the runtime caps one guest region at 256 MiB).</summary>
    public long MaxAllocation { get; init; } = 256L * 1024 * 1024;
    /// <summary>Host answer for user32!MessageBoxA. Null: the API faults (no window system; the user is never assumed to have clicked).</summary>
    public Win32MessageBoxHandler? MessageBoxHandler { get; init; }

    /// <summary>Diagnostics hook: called with "dll!name" before each Win32 API runs (thread id included in the text). Null (the default) costs nothing.</summary>
    public Action<string>? CallTrace { get; init; }
    /// <summary>Receives OutputDebugStringA text (what an attached debugger would see). Null: no debugger is attached and the text is dropped, as on Windows.</summary>
    public Action<string>? DebugOutput { get; init; }

    /// <summary>DirectSound reached through <c>dsound.dll!DirectSoundCreate</c> (see docs/binary-com.md). Default: the host's native dsound on a Windows host (including Wine), unavailable elsewhere (DSERR_NODRIVER). None: always DSERR_NODRIVER.</summary>
    public DirectSoundBackendKind DirectSound { get; init; } = DirectSoundBackendKind.Default;

    /// <summary>
    /// user32/gdi32 (windows, messages, GDI text) and the DirectX COM creation functions. Default: bridged to the host's native DLLs on a Windows host (including Wine), absent elsewhere
    /// (docs/binary-user32-bridge.md). None: the previous sandbox subset (only the desktop window exists, every other window API faults by name). Host on a non-Windows host throws.
    /// </summary>
    public HostWindowingKind HostWindowing { get; init; } = HostWindowingKind.Default;

    /// <summary>True when <see cref="HostWindowing"/> selects the host bridge on this host.</summary>
    public bool UsesHostWindowing => HostWindowing switch
    {
        HostWindowingKind.None => false,
        HostWindowingKind.Host => OperatingSystem.IsWindows() ? true : throw new Win32UsageException("Win32Options.HostWindowing = Host needs a Windows host (Windows or Wine)."),
        _ => OperatingSystem.IsWindows(),
    };

    /// <summary>waveOut backend (see docs/binary-audio.md). Default: HostWinmm on a Windows host (including Wine), None elsewhere. The environment variable BLITZCS_AUDIO=host|virtual|none overrides it.</summary>
    public WaveOutBackendKind WaveOut { get; init; } = WaveOutBackendKind.Default;
    /// <summary>VirtualSink: records every consumed buffer to this RIFF WAV file. HostWinmm: records every buffer written. Ignored (with a diagnostic) for the None backend.</summary>
    public string? WaveOutRecordPath { get; init; }
    /// <summary>Receives the PCM the guest played (see <see cref="WaveOutWrite"/>), on the backend's threads.</summary>
    public WaveOutObserver? WaveOutObserver { get; init; }
    /// <summary>VirtualSink only: the raw consume-time slices (about 10 ms each). HostWinmm cannot know consumption times and never calls it.</summary>
    public WaveOutObserver? WaveOutSliceObserver { get; init; }
}

/// <summary>Per-process emulation state shared by the services. One instance serves one guest process: a single module, or all modules of a <see cref="GuestModuleSet"/> when the resolvers were made with <see cref="Win32ImportResolver.SharedFactory"/>.</summary>
public sealed class Win32Context : IDisposable
{
    private readonly object bindGate = new();
    private IWin32Memory? memory;
    private object? memoryOwner;
    private readonly ThreadLocal<uint> lastError = new(() => 0);
    private readonly ThreadLocal<uint> highResult = new(() => 0);
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

    private static readonly Action<string>? EnvironmentTrace = Environment.GetEnvironmentVariable("BLITZCS_WIN32_TRACE") == "1" ? (m => { Console.Error.WriteLine(m); Console.Error.Flush(); }) : null;

    /// <summary>The call trace sink: <see cref="Win32Options.CallTrace"/>, else standard error when the environment variable BLITZCS_WIN32_TRACE=1 is set (diagnostics of generated programs), else null (free).</summary>
    public Action<string>? Trace => Options.CallTrace ?? EnvironmentTrace;

    private IDisposable[] disposables = [];
    private readonly object disposablesGate = new();

    /// <summary>Registers an object to dispose together with this context (after the handle table).</summary>
    internal void AddDisposable(IDisposable d) { lock (disposablesGate) disposables = [.. disposables, d]; }

    private Exception? processFault;
    private int exitingThreadId;
    private readonly ManualResetEventSlim exitEvent = new(false);

    internal bool IsExiting => Volatile.Read(ref exitingThreadId) != 0;
    internal bool IsExitingOnAnotherThread => Volatile.Read(ref exitingThreadId) is var t && t != 0 && t != Environment.CurrentManagedThreadId;

    /// <summary>
    /// Set when a guest thread terminated abnormally. On Windows an unhandled fault ends the whole process; here the process is the host, so
    /// instead every later Win32 call is refused with this cause. A thread that waits for the dead one fails promptly and explicitly instead of hanging.
    /// </summary>
    public Exception? ProcessFault => Volatile.Read(ref processFault);

    public void PoisonProcess(Exception cause) => Interlocked.CompareExchange(ref processFault, cause, null);

    /// <summary>
    /// Stops background host timers and audio streams, releases any critical sections held by the exiting thread, and wakes background guest threads parked in waits so they exit
    /// <see cref="GuestModule.CallAddress"/> before module detachment runs.
    /// </summary>
    public void BeginProcessExit()
    {
        Interlocked.CompareExchange(ref exitingThreadId, Environment.CurrentManagedThreadId, 0);
        exitEvent.Set();
        WinmmService.StopTimers(this);
        Sync.AbortForProcessExit();
        foreach (var v in handles.Values)
        {
            switch (v)
            {
                case KernelEvent ke: ke.Abort(); break;
                case KernelMutex km: km.Abort(); break;
                case KernelThread kt: kt.Done.Abort(); break;
                case WaveOutStream ws: ws.Dispose(); break;
            }
        }
    }

    internal void Sleep(int milliseconds)
    {
        if (milliseconds <= 0) { Thread.Yield(); return; }
        if (exitEvent.Wait(milliseconds) && IsExitingOnAnotherThread)
            throw new Win32UsageException("The guest process is exiting; background guest threads are terminated.");
    }

    internal void ThrowIfPoisoned()
    {
        if (IsExitingOnAnotherThread)
            throw new Win32UsageException("The guest process is exiting; background guest threads are terminated.");
        if (Volatile.Read(ref processFault) is { } f)
            throw new Win32UsageException($"A guest thread terminated abnormally ({f.GetType().Name}: {f.Message}); further Win32 calls are refused because the program state is undefined.");
    }
    internal HeapState Heaps { get; }
    internal VirtualState Virtual { get; }
    internal SyncState Sync { get; }
    internal TlsState Tls { get; }
    internal ProcessState Process { get; }
    internal FileState Files { get; }
    internal FlsState Fls { get; } = new();

    public uint LastError { get => lastError.Value; set => lastError.Value = value; }

    /// <summary>High 32 bits of the last 64-bit result of the calling thread (see <see cref="Win32Call.ReturnHigh"/>).</summary>
    public uint HighResult { get => highResult.Value; internal set => highResult.Value = value; }

    /// <summary>Per-process random value that EncodePointer/DecodePointer mix into pointers (Windows keeps a similar process cookie).</summary>
    internal uint PointerCookie { get; } = (uint)(System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue) |  0x10001);

    /// <summary>Serialises the lock-free list (SList) operations, which Windows performs with interlocked instructions on guest memory.</summary>
    internal object SListGate { get; } = new();

    /// <summary>Sets the last error and returns <paramref name="result"/> (FALSE/NULL by default).</summary>
    public uint Fail(uint error, uint result = 0) { LastError = error; return result; }

    /// <summary>Makes a guest or host module visible to GetModuleHandleA/LoadLibraryA/GetProcAddress. Exports map names (or "#ordinal") to guest addresses.</summary>
    public Win32Module RegisterModule(string name, uint handle, string path, IReadOnlyDictionary<string, uint>? exports = null) => Process.Register(name, handle, path, exports);

    /// <summary>Runtime hook (not available yet): returns a guest-callable address for an emulated export so GetProcAddress can hand it out.</summary>
    public void SetExportStubAddressProvider(Func<string, string, uint?>? provider) => Process.ExportStubAddress = provider;

    /// <summary>Looks an implemented API up by DLL and name (set by the resolver that owns the registry): lets GetProcAddress hand out a callable stub for it.</summary>
    internal Func<string, string, Win32Api?>? ApiLookup { get; set; }

    public uint CurrentThreadId => 1000u + 4u * (uint)Environment.CurrentManagedThreadId;

    private bool sharedByModules;
    internal bool IsSharedByModules => sharedByModules;
    private GuestMemory? anchor;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GuestMemory, IWin32Memory> moduleMemories = new();

    /// <summary>
    /// Declares that several modules of one guest process use this context (<see cref="Win32ImportResolver.ForAnotherModule"/>). Their memories are all accepted; reads and
    /// writes go through the calling module's own memory (which sees every module's regions), allocations (heaps, virtual memory, process-lifetime strings) come from the
    /// first module that used the context, which is pinned (<see cref="GuestMemory.PinAsProcessAnchor"/>): it cannot be unloaded while other modules are loaded.
    /// </summary>
    internal void MarkShared() { lock (bindGate) { sharedByModules = true; anchor?.PinAsProcessAnchor(); } }

    /// <summary>Binds the context to the memory of the first guest module that calls it; another module is refused unless the context is shared by the modules of one process.</summary>
    internal IWin32Memory Bind(GuestMemory guest)
    {
        lock (bindGate)
        {
            if (memory is null)
            {
                memoryOwner = guest; anchor = guest; memory = new GuestMemoryAdapter(guest);
                if (sharedByModules) guest.PinAsProcessAnchor();
            }
            if (ReferenceEquals(memoryOwner, guest)) return memory;
            if (!sharedByModules) throw new Win32UsageException("This Win32ImportResolver already serves another guest module; create one resolver per module, or share the process state with Win32ImportResolver.SharedFactory / ForAnotherModule.");
            return moduleMemories.GetValue(guest, g => new GuestMemoryAdapter(g, anchor));
        }
    }

    internal void BindForHost(IWin32Memory mem)
    {
        lock (bindGate)
        {
            if (memory is null) { memory = mem; memoryOwner = mem; }
            else if (!ReferenceEquals(memoryOwner, mem) && !ReferenceEquals(memory, mem) && !(mem is GuestMemoryAdapter a && ReferenceEquals(memoryOwner, a.Memory)))
                throw new Win32UsageException("This context is already bound to another memory.");
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
        foreach (var d in Interlocked.Exchange(ref disposables, []).Reverse()) d.Dispose();
        exitEvent.Dispose();
        lastError.Dispose();
        highResult.Dispose();
    }
}
