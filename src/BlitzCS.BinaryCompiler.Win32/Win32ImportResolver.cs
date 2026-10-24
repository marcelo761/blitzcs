using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

public enum Win32ImportStatus { Implemented, Missing }

/// <summary>Verdict for one import of a PE image.</summary>
public sealed record Win32ImportVerdict(string Dll, string? Name, ushort? Ordinal, Win32ImportStatus Status, string? Service)
{
    public string Label => $"{Dll}!{Name ?? "#" + Ordinal}";
}

/// <summary>Implemented or missing, for one entry of the priority list.</summary>
public sealed record Win32CoverageEntry(string Dll, string Name, int NeededBy, bool Implemented, string? Service);

public sealed class Win32Coverage
{
    public required IReadOnlyList<Win32Api> Implemented { get; init; }
    public required IReadOnlyList<Win32CoverageEntry> Priority { get; init; }
    public IEnumerable<Win32CoverageEntry> Missing => Priority.Where(p => !p.Implemented);
}

/// <summary>
/// Resolves PE imports to host implementations composed from independent <see cref="IWin32Service"/> modules. An API that no service registers is
/// not resolved (the loader reports it by name or the guest faults at call time naming dll!name): there are no placeholder stubs.
/// Create one resolver per guest module: it owns the emulated process state (heaps, handles, TLS, files) and its guest allocations.
/// </summary>
public sealed class Win32ImportResolver : IGuestImportResolver, IGuestDataImportResolver, IDisposable
{
    private readonly Win32Registry registry = new();

    public Win32ImportResolver(Win32Options? options = null, IEnumerable<IWin32Service>? services = null)
    {
        Services = [.. services ?? DefaultServices()];
        foreach (var s in Services) s.Register(registry);
        Context = new Win32Context(options ?? new Win32Options());
        Context.Process.RegisterSystem(registry.All.Select(a => a.Dll));
        Context.ApiLookup = (dll, name) => registry.Find(dll, name, null);
    }

    /// <summary>The standard composition. Each service can be omitted or replaced independently.</summary>
    public static IEnumerable<IWin32Service> DefaultServices() =>
    [
        new HeapService(), new MemoryService(), new SyncService(), new TlsService(), new ProcessService(),
        new StringService(), new FileService(), new CrtService(), new WinsockService(),
        new Advapi32RegistryService(),
        new Ole32Service(),
        new User32BasicService(),
        new MsvcrtRuntimeService(),
        new Kernel32ExtraService(),
        new WinmmService(),
        new DirectSoundService(),
        new WinsockCoreService(),
    ];

    public IReadOnlyList<IWin32Service> Services { get; }
    public Win32Context Context { get; }
    public IReadOnlyList<Win32Api> Apis => registry.All;

    /// <summary>Exported variables (msvcrt __mb_cur_max, _pctype ...): the IAT slot receives the variable's guest address.</summary>
    public GuestDataImport? ResolveData(string dll, string? name, ushort? ordinal)
    {
        var factory = registry.FindData(dll, name);
        if (factory is null) return null;
        var ctx = Context;
        return new GuestDataImport(memory => factory(ctx, ctx.Bind(memory)));
    }

    public GuestImport? Resolve(string dll, string? name, ushort? ordinal)
    {
        var api = registry.Find(dll, name, ordinal);
        if (api is null) return null;
        var ctx = Context;
        return api.Convention == Win32Convention.Stdcall
            ? GuestImportBuilder.StdcallCall(api.ArgCount, call => Invoke(ctx, api, call))
            : GuestImportBuilder.CdeclCall(call => Invoke(ctx, api, call));
    }

    internal static uint Invoke(Win32Context ctx, Win32Api api, GuestCall guest)
    {
        ctx.Trace?.Invoke($"[{Environment.CurrentManagedThreadId}] {api.Dll}!{api.Name}");
        ctx.ThrowIfPoisoned();
        var mem = ctx.Bind(guest.Memory);
        Span<uint> args = stackalloc uint[api.ArgCount];
        for (var i = 0; i < args.Length; i++) args[i] = guest.Arg(i);
        return api.Handler(new Win32Call(ctx, mem, args, api.Variadic ? guest.Arg : null, guest));
    }

    /// <summary>
    /// Invokes an implemented API directly on a host-provided memory, without a guest module (host tools, tests). Variadic APIs accept extra
    /// arguments. Throws <see cref="KeyNotFoundException"/> when the API is not implemented.
    /// </summary>
    public uint Call(IWin32Memory memory, string dll, string name, params uint[] args)
    {
        var api = registry.Find(dll, name, null) ?? throw new KeyNotFoundException($"{dll}!{name} is not implemented.");
        if (args.Length < api.ArgCount || (!api.Variadic && args.Length != api.ArgCount))
            throw new ArgumentException($"{api} takes {api.ArgCount} argument(s), got {args.Length}.");
        Context.Trace?.Invoke($"[{Environment.CurrentManagedThreadId}] {api.Dll}!{api.Name}");
        Context.ThrowIfPoisoned();
        Context.BindForHost(memory);
        var captured = args;
        return api.Handler(new Win32Call(Context, memory, args.AsSpan(0, api.ArgCount), api.Variadic ? i => captured[i] : null));
    }

    /// <summary>Static view of what the standard composition implements, against the priority list taken from real binaries.</summary>
    public static Win32Coverage Coverage { get; } = BuildCoverage();

    private static Win32Coverage BuildCoverage()
    {
        var registry = new Win32Registry();
        foreach (var s in DefaultServices()) s.Register(registry);
        var priority = Win32PriorityList.Entries.Select(e =>
        {
            var api = e.Name.StartsWith('#') ? registry.Find(e.Dll, null, ushort.Parse(e.Name[1..])) : registry.Find(e.Dll, e.Name, null);
            return new Win32CoverageEntry(e.Dll, e.Name, e.NeededBy, api is not null, api?.Service);
        }).ToList();
        return new Win32Coverage { Implemented = registry.All, Priority = priority };
    }

    /// <summary>For every import of the image: is it implemented by this resolver? Imports from non-Win32 modules (other guest DLLs) are reported as Missing here and must be satisfied by the caller's own resolver.</summary>
    public IReadOnlyList<Win32ImportVerdict> Analyze(PeImage image) =>
    [
        .. image.Imports.Select(i =>
        {
            var api = registry.Find(i.Dll, i.Name, i.Ordinal);
            return new Win32ImportVerdict(i.Dll, i.Name, i.Ordinal, api is null && registry.FindData(i.Dll, i.Name) is null ? Win32ImportStatus.Missing : Win32ImportStatus.Implemented, api?.Service ?? (api is null ? "data" : null));
        }),
    ];

    public void Dispose() => Context.Dispose();
}
