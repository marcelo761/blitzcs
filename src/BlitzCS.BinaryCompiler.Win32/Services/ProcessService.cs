using System.Diagnostics;
using BlitzCS.BinaryCompiler.Runtime;
using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>A module the guest can find through GetModuleHandle/LoadLibrary/GetProcAddress. Exports map names (or "#ordinal") to guest addresses.</summary>
public sealed record Win32Module(string Name, uint Handle, string Path, IReadOnlyDictionary<string, uint> Exports);

internal sealed class ProcessState(Win32Context ctx)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Win32Module> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Win32Module> byHandle = [];
    private readonly Dictionary<uint, IWin32Allocation> environmentBlocks = [];
    private readonly List<IWin32Allocation> permanent = [];
    private uint commandLineA;
    private uint unhandledFilter;
    private uint nextSystemHandle = 0x7C000000;
    public Func<string, string, uint?>? ExportStubAddress { get; set; }
    public static readonly long StartTicks = Stopwatch.GetTimestamp();

    public Win32Module Register(string name, uint handle, string path, IReadOnlyDictionary<string, uint>? exports)
    {
        var module = new Win32Module(Win32Registry.NormalizeDll(name), handle, path, exports ?? new Dictionary<string, uint>());
        lock (gate)
        {
            if (modules.ContainsKey(module.Name)) throw new InvalidOperationException($"Module {module.Name} is already registered.");
            if (byHandle.ContainsKey(handle)) throw new InvalidOperationException($"Module handle 0x{handle:X8} is already registered.");
            modules[module.Name] = module; byHandle[handle] = module;
        }
        return module;
    }

    /// <summary>Makes each emulated DLL a loadable module (handles are opaque tokens: they do not map a PE image).</summary>
    public void RegisterSystem(IEnumerable<string> dlls)
    {
        foreach (var dll in dlls.Select(Win32Registry.NormalizeDll).Distinct().Order(StringComparer.Ordinal))
        {
            lock (gate) { if (modules.ContainsKey(dll)) continue; }
            Register(dll, nextSystemHandle, "C:\\WINDOWS\\system32\\" + dll, null);
            nextSystemHandle += 0x10000;
        }
    }

    private readonly Dictionary<(string, string), uint> stubs = [];

    /// <summary>The guest-callable address of an emulated API: one dynamic stub per (dll, name), created on first request and reused afterwards (GetProcAddress of the same function twice returns the same pointer).</summary>
    public uint StubFor(GuestModule module, Win32Api api)
    {
        lock (gate)
        {
            var key = (Win32Registry.NormalizeDll(api.Dll), api.Name);
            if (stubs.TryGetValue(key, out var existing)) return existing;
            var context = ctx;
            var address = module.AllocateStub(call => Win32ImportResolver.Invoke(context, api, call), api.Convention == Win32Convention.Stdcall ? 4u * (uint)api.ArgCount : 0u);
            stubs[key] = address;
            return address;
        }
    }

    public Win32Module? Find(string name) { lock (gate) return modules.GetValueOrDefault(Win32Registry.NormalizeDll(name)); }
    public Win32Module? Find(uint handle) { lock (gate) return byHandle.GetValueOrDefault(handle); }
    public bool IsSystem(Win32Module m) => m.Path.StartsWith("C:\\WINDOWS\\system32\\", StringComparison.Ordinal) && m.Exports.Count == 0;

    public uint CommandLineA(IWin32Memory mem)
    {
        lock (gate)
        {
            if (commandLineA != 0) return commandLineA;
            var bytes = Nls.EncodeChars(ctx.Options.AnsiCodePage, ctx.Options.CommandLine + "\0", (byte)'?', false, out _, out _);
            var a = mem.Allocate(bytes.Length); a.Write(mem, bytes); permanent.Add(a);
            return commandLineA = a.Address;
        }
    }

    public uint NewEnvironmentBlock(IWin32Memory mem, bool wide)
    {
        var text = new StringBuilder();
        foreach (var (k, v) in ctx.Options.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) text.Append(k).Append('=').Append(v).Append('\0');
        text.Append('\0');
        if (ctx.Options.Environment.Count == 0) text.Append('\0');
        var bytes = wide ? Nls.ToUtf16(text.ToString()) : Nls.EncodeChars(ctx.Options.AnsiCodePage, text.ToString(), (byte)'?', false, out _, out _);
        var a = mem.Allocate(bytes.Length); a.Write(mem, bytes);
        lock (gate) environmentBlocks[a.Address] = a;
        return a.Address;
    }

    public bool FreeEnvironmentBlock(uint address)
    {
        IWin32Allocation? a;
        lock (gate) { if (!environmentBlocks.Remove(address, out a)) return false; }
        a.Dispose();
        return true;
    }

    private readonly Dictionary<(string, int), uint> threadBuffers = [];

    /// <summary>A zeroed per-thread, per-purpose guest buffer that lives as long as the context (used for static results such as inet_ntoa's).</summary>
    public uint ThreadBuffer(IWin32Memory mem, string purpose, int size)
    {
        lock (gate)
        {
            if (threadBuffers.TryGetValue((purpose, Environment.CurrentManagedThreadId), out var existing)) return existing;
            var a = mem.Allocate(size); permanent.Add(a);
            return threadBuffers[(purpose, Environment.CurrentManagedThreadId)] = a.Address;
        }
    }

    public uint ExchangeUnhandledFilter(uint filter) { lock (gate) { var old = unhandledFilter; unhandledFilter = filter; return old; } }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var a in permanent.Concat(environmentBlocks.Values)) { try { a.Dispose(); } catch (ObjectDisposedException) { } catch (Runtime.GuestException) { } }
            permanent.Clear(); environmentBlocks.Clear();
        }
    }
}

internal static class AllocationExtensions
{
    public static void Write(this IWin32Allocation a, IWin32Memory mem, byte[] bytes) => mem.Write(a.Address, bytes);
}

/// <summary>
/// KERNEL32 process, module and system information (Microsoft Learn, "Process and Thread Functions" / "System Information Functions").
/// ExitProcess/TerminateProcess(self) NEVER end the host: they throw <see cref="Win32ExitProcessException"/>, abandoning the guest call.
/// GetVersion/GetVersionEx report <see cref="Win32Options.Version"/> (default Windows XP SP3: 5.1.2600, platform NT). GetProcAddress/LoadLibraryA/
/// GetModuleHandleA see (a) emulated DLLs as opaque module tokens and (b) host-registered modules with explicit export tables; a function that is
/// not exported returns NULL with ERROR_PROC_NOT_FOUND (127) and an unknown DLL NULL with ERROR_MOD_NOT_FOUND (126). Exposing the emulated functions
/// themselves as callable addresses needs a runtime hook (stub address of a bound import) supplied through
/// <see cref="Win32Context.SetExportStubAddressProvider"/>; without it GetProcAddress on an emulated DLL honestly reports "not found".
/// The environment block, command line and module file name come from <see cref="Win32Options"/>; the host environment is never leaked.
/// IsBad*Ptr use mapped-range checks (writability is not distinguished). Thread ids are derived from managed thread ids.
/// </summary>
public sealed class ProcessService : Win32Service
{
    public override string Name => "Kernel32.Process";

    private static uint GetVersionEx(Win32Call c)
    {
        var size = c.Mem.ReadUInt32(c[0]);
        if (size != 148 && size != 156) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        var v = c.Ctx.Options.Version;
        var data = new byte[size];
        BitConverter.TryWriteBytes(data.AsSpan(0), size);
        BitConverter.TryWriteBytes(data.AsSpan(4), v.Major);
        BitConverter.TryWriteBytes(data.AsSpan(8), v.Minor);
        BitConverter.TryWriteBytes(data.AsSpan(12), v.Build);
        BitConverter.TryWriteBytes(data.AsSpan(16), 2u); // VER_PLATFORM_WIN32_NT
        Encoding.Latin1.GetBytes(v.Csd.AsSpan(0, Math.Min(v.Csd.Length, 127)), data.AsSpan(20));
        if (size == 156)
        {
            BitConverter.TryWriteBytes(data.AsSpan(148), v.ServicePackMajor);
            BitConverter.TryWriteBytes(data.AsSpan(152), (ushort)1); // VER_NT_WORKSTATION
        }
        c.Mem.Write(c[0], data);
        return 1;
    }

    private static uint ModuleFileName(Win32Call c)
    {
        string? path;
        if (c[0] == 0 || c[0] == c.Ctx.Options.MainModuleBase) path = c.Ctx.Options.MainModulePath;
        else path = c.Ctx.Process.Find(c[0])?.Path;
        if (path is null) return c.Ctx.Fail(Win32Error.ModNotFound);
        return CopyString(c, path, c[1], c[2]);
    }

    private static uint CopyString(Win32Call c, string text, uint buffer, uint size)
    {
        if (size == 0 || buffer == 0) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        var bytes = Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, text, (byte)'?', false, out _, out _);
        if (bytes.Length < size)
        {
            c.Mem.Write(buffer, [.. bytes, (byte)0]);
            return (uint)bytes.Length;
        }
        c.Mem.Write(buffer, [.. bytes.AsSpan(0, (int)size - 1).ToArray(), (byte)0]);
        c.Ctx.LastError = Win32Error.InsufficientBuffer;
        return size;
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "GetLastError", 0, c => c.Ctx.LastError);
        Std(K, "SetLastError", 1, c => { c.Ctx.LastError = c[0]; return 0; });
        Std(K, "GetCurrentProcess", 0, _ => 0xFFFFFFFF);
        Std(K, "GetCurrentThread", 0, _ => 0xFFFFFFFE);
        Std(K, "GetCurrentThreadId", 0, c => c.Ctx.CurrentThreadId);
        Std(K, "GetCurrentProcessId", 0, _ => (uint)Environment.ProcessId);
        Std(K, "Sleep", 1, c => { if (c[0] == uint.MaxValue) throw new Win32UsageException("Sleep(INFINITE) would block the host forever."); Thread.Sleep((int)Math.Min(c[0], int.MaxValue)); return 0; });
        Std(K, "GetTickCount", 0, c => (uint)(Stopwatch.GetElapsedTime(ProcessState.StartTicks).TotalMilliseconds));
        Std(K, "QueryPerformanceCounter", 1, c =>
        {
            if (c[0] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Mem.WriteUInt64(c[0], (ulong)Stopwatch.GetTimestamp());
            return 1;
        });
        Std(K, "QueryPerformanceFrequency", 1, c =>
        {
            if (c[0] == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Mem.WriteUInt64(c[0], (ulong)Stopwatch.Frequency);
            return 1;
        });
        Std(K, "ExitProcess", 1, c => throw new Win32ExitProcessException(c[0], "ExitProcess"));
        Std(K, "TerminateProcess", 2, c => c[0] == 0xFFFFFFFF ? throw new Win32ExitProcessException(c[1], "TerminateProcess") : c.Ctx.Fail(Win32Error.InvalidHandle));
        Std(K, "GetVersion", 0, c => { var v = c.Ctx.Options.Version; return v.Build << 16 | v.Minor << 8 | v.Major; });
        Std(K, "GetVersionExA", 1, GetVersionEx);
        Std(K, "GetCommandLineA", 0, c => c.Ctx.Process.CommandLineA(c.Mem));
        Std(K, "GetEnvironmentStrings", 0, c => c.Ctx.Process.NewEnvironmentBlock(c.Mem, false));
        Std(K, "GetEnvironmentStringsW", 0, c => c.Ctx.Process.NewEnvironmentBlock(c.Mem, true));
        Std(K, "FreeEnvironmentStringsA", 1, c => c.Ctx.Process.FreeEnvironmentBlock(c[0]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
        Std(K, "FreeEnvironmentStringsW", 1, c => c.Ctx.Process.FreeEnvironmentBlock(c[0]) ? 1u : c.Ctx.Fail(Win32Error.InvalidParameter));
        Std(K, "GetEnvironmentVariableA", 3, c =>
        {
            var name = c.Mem.ReadAnsiString(c[0], 32767);
            var env = c.Ctx.Options.Environment;
            var key = env.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null) return c.Ctx.Fail(Win32Error.EnvVarNotFound);
            var bytes = Nls.EncodeChars(c.Ctx.Options.AnsiCodePage, env[key], (byte)'?', false, out _, out _);
            if (c[2] <= bytes.Length) return (uint)bytes.Length + 1; // too small: required size including the terminator
            c.Mem.Write(c[1], [.. bytes, (byte)0]);
            c.Ctx.LastError = Win32Error.Success;
            return (uint)bytes.Length;
        });
        Std(K, "GetStartupInfoA", 1, c => { var si = new byte[68]; si[0] = 68; c.Mem.Write(c[0], si); return 0; });
        Std(K, "GetModuleFileNameA", 3, ModuleFileName);
        Std(K, "GetModuleHandleA", 1, c =>
        {
            if (c[0] == 0) return c.Ctx.Options.MainModuleBase;
            var module = c.Ctx.Process.Find(c.Mem.ReadAnsiString(c[0], 260));
            return module?.Handle ?? c.Ctx.Fail(Win32Error.ModNotFound);
        });
        Std(K, "LoadLibraryA", 1, c =>
        {
            var name = c.Mem.ReadAnsiString(c[0], 260);
            if (name.Length == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
            return c.Ctx.Process.Find(name)?.Handle ?? c.Ctx.Fail(Win32Error.ModNotFound);
        });
        Std(K, "FreeLibrary", 1, c => c.Ctx.Process.Find(c[0]) is null ? c.Ctx.Fail(Win32Error.InvalidHandle) : 1u);
        Std(K, "GetProcAddress", 2, c =>
        {
            var module = c.Ctx.Process.Find(c[0]);
            if (module is null) return c.Ctx.Fail(Win32Error.ModNotFound);
            var export = c[1] < 0x10000 ? "#" + c[1] : c.Mem.ReadAnsiString(c[1], 1024);
            if (module.Exports.TryGetValue(export, out var address)) return address;
            if (c.Ctx.Process.IsSystem(module) && c[1] >= 0x10000 && c.Ctx.Process.ExportStubAddress?.Invoke(module.Name, export) is { } stub) return stub;
            if (c.Ctx.Process.IsSystem(module) && c[1] >= 0x10000 && c.Ctx.ApiLookup?.Invoke(module.Name, export) is { } api)
            {
                // An emulated export becomes a guest-callable function pointer: a dynamic stub of the running module bound to the same handler as the import.
                var guestModule = GuestModule.Current;
                if (guestModule is not null && guestModule.StubCapacity > 0) return c.Ctx.Process.StubFor(guestModule, api);
                c.Ctx.Options.DebugOutput?.Invoke($"GetProcAddress({module.Name}!{export}): the function exists but this module was translated without a dynamic stub pool, so it has no callable address (re-translate with DynamicStubs > 0).");
            }
            return c.Ctx.Fail(Win32Error.ProcNotFound);
        });
        Std(K, "SetUnhandledExceptionFilter", 1, c => c.Ctx.Process.ExchangeUnhandledFilter(c[0]));
        Std(K, "IsBadReadPtr", 2, c => c[1] == 0 ? 0u : c.Mem.IsMapped(c[0], c[1]) ? 0u : 1u);
        Std(K, "IsBadWritePtr", 2, c => c[1] == 0 ? 0u : c.Mem.IsMapped(c[0], c[1]) ? 0u : 1u);
        Std(K, "IsBadCodePtr", 1, c => c.Mem.IsMapped(c[0], 1) ? 0u : 1u);
    }
}
