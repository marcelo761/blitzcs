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
    /// <summary>What the resolvers of one guest process share: the API registry, the service instances and the Win32 context (heaps, handle table, TLS/FLS index space, critical sections, files, registry ...). Reference counted: the context dies with the last resolver.</summary>
    private sealed class SharedState
    {
        public readonly Win32Registry Registry = new();
        public IReadOnlyList<IWin32Service> Services = [];
        public Win32Context Context = null!;
        public int Refs;
        public readonly object Gate = new();
    }

    private readonly SharedState state;
    private readonly Win32Registry registry;
    private int disposed;

    public Win32ImportResolver(Win32Options? options = null, IEnumerable<IWin32Service>? services = null)
    {
        state = new SharedState { Refs = 1 };
        registry = state.Registry;
        Services = state.Services = [.. services ?? DefaultServices(options)];
        foreach (var s in Services) s.Register(registry);
        Context = state.Context = new Win32Context(options ?? new Win32Options());
        Context.Process.RegisterSystem(registry.All.Select(a => a.Dll));
        Context.ApiLookup = (dll, name) => registry.Find(dll, name, null);
    }

    /// <summary>A view over the shared state of one process (one Win32Context, one set of service instances).</summary>
    private Win32ImportResolver(SharedState shared)
    {
        state = shared; registry = shared.Registry; Services = shared.Services; Context = shared.Context;
    }

    /// <summary>
    /// Another resolver for a further module of the process this resolver serves. Windows has one heap, handle table, TLS/FLS index space, environment, current
    /// directory and module list per process, so the modules of one <see cref="GuestModuleSet"/> must see the same Win32 state: everything is shared and
    /// only per-module identity stays with the module (<c>GuestModule.Current</c>: the stub provider, DllMain bookkeeping, the module handle). Disposing a view
    /// releases its reference; the context is disposed with the last one.
    /// </summary>
    public Win32ImportResolver ForAnotherModule()
    {
        lock (state.Gate)
        {
            if (state.Refs == 0) throw new ObjectDisposedException(nameof(Win32ImportResolver), "The Win32 state of this process has been disposed.");
            state.Refs++;
        }
        state.Context.MarkShared();
        return new Win32ImportResolver(state);
    }

    /// <summary>
    /// A resolver factory for <see cref="GuestModuleSet(Func{string, IGuestImportResolver}, ModuleLinkOptions?)"/> in which every module of the set gets a resolver over
    /// the same Win32 state (the first call creates it from <paramref name="options"/>). Use it instead of <c>name =&gt; new Win32ImportResolver(options)</c> unless the modules
    /// are meant to be separate processes.
    /// </summary>
    public static Func<string, IGuestImportResolver> SharedFactory(Win32Options? options = null, IEnumerable<IWin32Service>? services = null)
    {
        Win32ImportResolver? first = null;
        var gate = new object();
        return _ =>
        {
            lock (gate)
            {
                if (first is null || first.state.Refs == 0) return first = new Win32ImportResolver(options, services);   // a fresh process once the previous one is gone
                return first.ForAnotherModule();
            }
        };
    }

    /// <summary>
    /// The standard composition. Each service can be omitted or replaced independently. With <see cref="Win32Options.UsesHostWindowing"/> (the default on a Windows host) user32/gdi32 are
    /// the host bridge (<see cref="HostUser32Service"/>, <see cref="HostGdi32Service"/>, <see cref="HostDirectXService"/>) and the sandbox subset keeps only MessageBoxA; elsewhere those
    /// services are not part of the composition at all, so their APIs stay absent and fault by name.
    /// </summary>
    public static IEnumerable<IWin32Service> DefaultServices(Win32Options? options = null)
    {
        var host = (options ?? new Win32Options()).UsesHostWindowing;
        return [.. Common(host), .. host ? new IWin32Service[] { new HostUser32Service(), new HostGdi32Service(), new HostDirectXService() } : []];
    }

    private static IEnumerable<IWin32Service> Common(bool hostWindowing) =>
    [
        new HeapService(), new MemoryService(), new SyncService(), new TlsService(), new ProcessService(),
        new StringService(), new FileService(), new CrtService(), new WinsockService(),
        new Advapi32RegistryService(),
        new Ole32Service(),
        new User32BasicService(messageBoxOnly: hostWindowing),
        new MsvcrtRuntimeService(),
        new Kernel32ExtraService(),
        new Kernel32StartupService(),
        new Kernel32SrwService(),
        new Kernel32ResourceService(),
        new Kernel32TimeService(),
        new Kernel32WideService(),
        new FilePathService(),
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        bool last;
        lock (state.Gate) last = --state.Refs == 0;
        if (last) Context.Dispose();
    }
}
