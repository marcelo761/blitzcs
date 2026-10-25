namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// Several translated modules in one process, linked to each other: an import of <c>other.dll!name</c> (or by ordinal) is bound to the export of the
/// sibling module <c>other</c>, whose code runs in its own address space region with its own TEB/PEB/stacks. See docs/binary-multimodule.md.
/// Imports not satisfied by a sibling go to the host resolver (for example the Win32 component's resolver), which is consulted first.
/// Modules are loaded in dependency order (DllMain of a dependency has run before its dependents load) and disposed in reverse order.
/// Add/LoadAll/Unload/Dispose are not thread-safe against each other; calls into loaded modules are fully concurrent.
/// </summary>
public sealed class GuestModuleSet : IDisposable
{
    private sealed class Entry
    {
        public required string Key, DisplayName;
        public required TranslationArtifact Artifact;
        public required PeImage Image;
        public required ExportConventions Conventions;
        public required GuestModuleOptions Options;
        public GuestModule? Module;
        public IGuestImportResolver? Host;   // created by the factory for this module (disposed with the set)
        public List<Entry> Dependencies = [];
        public int LoadOrder;
    }

    private readonly IGuestImportResolver host;
    private readonly Func<string, IGuestImportResolver>? hostFactory;
    private readonly ModuleLinkOptions link;
    private readonly List<Entry> entries = [];
    private readonly List<BridgeCounter> counters = [];
    private int loadCounter;
    private readonly object gate = new();   // serialises Add/LoadAll/Unload: modules can be added while guest code of already loaded modules runs
    private bool disposed;

    public GuestModuleSet(IGuestImportResolver? hostImports = null, ModuleLinkOptions? options = null)
    {
        host = hostImports ?? new DictionaryGuestImportResolver();
        link = options ?? new ModuleLinkOptions();
        if (link.CdeclWindowDwords is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(options), "CdeclWindowDwords must be in 1..256.");
    }

    /// <summary>
    /// Like the other constructor, but creates one host resolver per module (<paramref name="hostImportsFor"/> receives the name passed to <see cref="Add"/>).
    /// Needed for resolvers that own per-process state such as the Win32 component's <c>Win32ImportResolver</c> ("one resolver per guest module").
    /// Resolvers created here that implement <see cref="IDisposable"/> are disposed by the set, after the modules.
    /// </summary>
    public GuestModuleSet(Func<string, IGuestImportResolver> hostImportsFor, ModuleLinkOptions? options = null) : this((IGuestImportResolver?)null, options)
    {
        hostFactory = hostImportsFor ?? throw new ArgumentNullException(nameof(hostImportsFor));
    }

    /// <summary>"C:\x\OGG.DLL", "ogg.dll" and "ogg" all name the same module (case-insensitive, extension optional).</summary>
    public static string NormalizeName(string name)
    {
        var n = name.Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return n.EndsWith(".dll", StringComparison.Ordinal) ? n[..^4] : n;
    }

    /// <summary>Registers a module (artifact and the matching, possibly rebased, image). Nothing is loaded until <see cref="LoadAll"/>.</summary>
    public GuestModuleSet Add(string name, TranslationArtifact artifact, PeImage image, ExportConventions? conventions = null, GuestModuleOptions? moduleOptions = null)
    {
        lock (gate) return AddCore(name, artifact, image, conventions, moduleOptions);
    }

    private GuestModuleSet AddCore(string name, TranslationArtifact artifact, PeImage image, ExportConventions? conventions, GuestModuleOptions? moduleOptions)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(image);
        var key = NormalizeName(name);
        if (entries.Any(e => e.Key == key)) throw new ArgumentException($"A module named '{name}' is already in the set.", nameof(name));
        if (conventions?.Default is { } d && d != GuestCallingConvention.Cdecl)
            throw new ArgumentException("ExportConventions.Default accepts only Cdecl; declare stdcall exports individually with their argument count.", nameof(conventions));
        entries.Add(new Entry { Key = key, DisplayName = name, Artifact = artifact, Image = image, Conventions = conventions ?? new ExportConventions(), Options = moduleOptions ?? new GuestModuleOptions() });
        return this;
    }

    /// <summary>The loaded module registered under <paramref name="name"/>.</summary>
    public GuestModule this[string name]
    {
        get
        {
            var e = entries.FirstOrDefault(x => x.Key == NormalizeName(name)) ?? throw new KeyNotFoundException($"No module named '{name}' in the set.");
            return e.Module ?? throw new InvalidOperationException($"Module '{name}' is registered but not loaded yet; call LoadAll().");
        }
    }

    /// <summary>Number of calls that went through each cross-module bridge so far, keyed "consumer->target!export" (proves a call really traversed modules).</summary>
    public IReadOnlyDictionary<string, long> CrossModuleCalls { get { lock (counters) return counters.ToDictionary(c => c.Label, c => Interlocked.Read(ref c.Calls)); } }

    public IReadOnlyList<GuestModule> LoadedModules => [.. entries.Where(e => e.Module is not null).OrderBy(e => e.LoadOrder).Select(e => e.Module!)];

    /// <summary>Loads every registered, not yet loaded module in dependency order. On failure everything loaded by this call is disposed again (reverse order) and the error is rethrown.</summary>
    public void LoadAll() { lock (gate) LoadAllCore(); }

    private void LoadAllCore()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var pending = entries.Where(e => e.Module is null).ToList();
        var order = TopologicalOrder(pending);
        var loadedNow = new List<Entry>();
        try
        {
            foreach (var entry in order)
            {
                var resolver = new SiblingResolver(this, entry);
                entry.Host ??= hostFactory?.Invoke(entry.DisplayName);
                entry.Module = GuestModule.Load(entry.Artifact, entry.Image, new ChainedGuestImportResolver(entry.Host ?? host, resolver), entry.Options);
                entry.Dependencies = resolver.Dependencies.ToList();
                foreach (var dep in entry.Dependencies) dep.Module!.AddDependent();
                foreach (var other in entries.Where(o => o != entry && o.Module is not null))   // one process, one address space: managed accesses see every module's regions
                {
                    entry.Module.Memory.LinkPeer(other.Module!.Memory);
                    other.Module.Memory.LinkPeer(entry.Module.Memory);
                }
                entry.LoadOrder = ++loadCounter;
                loadedNow.Add(entry);
            }
        }
        catch
        {
            for (var i = loadedNow.Count - 1; i >= 0; i--) Release(loadedNow[i]);
            throw;
        }
    }

    /// <summary>Disposes one module. Fails with <see cref="InvalidOperationException"/> while another loaded module is still linked to it.</summary>
    public void Unload(string name) { lock (gate) UnloadCore(name); }

    /// <summary>True when a module registered under <paramref name="name"/> exists in the set (loaded or not).</summary>
    public bool Contains(string name) { lock (gate) return entries.Any(x => x.Key == NormalizeName(name)); }

    /// <summary>Removes a module that was added but failed to load (or was never loaded), disposing the host resolver created for it. Loaded modules are untouched.</summary>
    public void Discard(string name)
    {
        lock (gate)
            foreach (var e in entries.Where(x => x.Key == NormalizeName(name) && x.Module is null).ToList())
            {
                entries.Remove(e);
                (e.Host as IDisposable)?.Dispose();
            }
    }

    private void UnloadCore(string name)
    {
        var e = entries.FirstOrDefault(x => x.Key == NormalizeName(name)) ?? throw new KeyNotFoundException($"No module named '{name}' in the set.");
        if (e.Module is null) throw new InvalidOperationException($"Module '{name}' is not loaded.");
        var users = entries.Where(x => x.Module is not null && x.Dependencies.Contains(e)).Select(x => x.DisplayName).ToList();
        if (users.Count > 0) throw new InvalidOperationException($"Module '{name}' cannot be unloaded: {string.Join(", ", users)} still import(s) from it. Unload the dependents first.");
        Release(e);
        (e.Host as IDisposable)?.Dispose();
        entries.Remove(e);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        List<Exception>? errors = null;
        foreach (var e in entries.Where(x => x.Module is not null).OrderByDescending(x => x.LoadOrder).ToList())
        {
            try { Release(e); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        foreach (var e in entries)
            if (e.Host is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        entries.Clear();
        if (errors is not null) throw new AggregateException("One or more modules failed to dispose.", errors);
    }

    /// <summary>
    /// Process-exit disposal (see <see cref="GuestModule.TryDispose"/>): modules are detached in reverse load order as in <see cref="Dispose"/>, but a module whose guest code is still
    /// running after <paramref name="graceMilliseconds"/> (and so every module it keeps alive) is left to the operating system instead of blocking the exit. Neither its DllMain detach
    /// nor its host environment (which would unmap memory under the running thread) is touched.
    /// </summary>
    internal void DisposeAtProcessExit(int graceMilliseconds)
    {
        if (disposed) return;
        disposed = true;
        var abandoned = new HashSet<Entry>();
        foreach (var e in entries.Where(x => x.Module is not null).OrderByDescending(x => x.LoadOrder).ToList())
        {
            var module = e.Module!;
            try
            {
                if (module.DependentCount != 0 || !TryRelease(e, module, graceMilliseconds)) abandoned.Add(e);
            }
            catch (Exception error) when (error is GuestException or InvalidOperationException or ObjectDisposedException) { abandoned.Add(e); }
        }
        foreach (var e in entries)
            if (!abandoned.Contains(e) && e.Host is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception error) when (error is GuestException or InvalidOperationException or ObjectDisposedException) { }
            }
        entries.Clear();
    }

    private bool TryRelease(Entry e, GuestModule module, int graceMilliseconds)
    {
        if (!module.TryDispose(graceMilliseconds)) return false;
        e.Module = null;
        foreach (var other in entries.Where(o => o != e && o.Module is not null)) { other.Module!.Memory.UnlinkPeer(module.Memory); module.Memory.UnlinkPeer(other.Module.Memory); }
        foreach (var dep in e.Dependencies) dep.Module?.RemoveDependent();
        e.Dependencies = [];
        return true;
    }

    private void Release(Entry e)
    {
        var module = e.Module;
        if (module is null) return;
        e.Module = null;
        foreach (var other in entries.Where(o => o != e && o.Module is not null)) { other.Module!.Memory.UnlinkPeer(module.Memory); module.Memory.UnlinkPeer(other.Module.Memory); }
        try { module.Dispose(); }
        finally { foreach (var dep in e.Dependencies) dep.Module?.RemoveDependent(); e.Dependencies = []; }
    }

    private List<Entry> TopologicalOrder(List<Entry> pending)
    {
        var order = new List<Entry>();
        var state = new Dictionary<Entry, int>();
        void Visit(Entry e, List<Entry> path)
        {
            if (state.TryGetValue(e, out var s))
            {
                if (s == 1) throw new GuestImportException("Circular module imports: " + string.Join(" -> ", path.SkipWhile(p => p != e).Append(e).Select(p => p.DisplayName)) + ". Modules must be acyclic so every DllMain runs after its dependencies' DllMain.");
                return;
            }
            state[e] = 1; path.Add(e);
            foreach (var dll in e.Artifact.Imports.Select(i => NormalizeName(i.Dll)).Distinct())
                if (entries.FirstOrDefault(x => x.Key == dll) is { } dep && (dep.Module is null))
                    Visit(dep, path);
            path.RemoveAt(path.Count - 1); state[e] = 2;
            order.Add(e);
        }
        foreach (var e in pending) Visit(e, []);
        return order;
    }

    private sealed class SiblingResolver(GuestModuleSet set, Entry consumer) : IGuestImportResolver
    {
        private readonly HashSet<Entry> dependencies = [];
        private readonly Dictionary<(Entry, string), GuestImport> bridges = [];
        public IEnumerable<Entry> Dependencies => dependencies;

        public GuestImport? Resolve(string dll, string? name, ushort? ordinal)
        {
            var target = set.entries.FirstOrDefault(x => x.Key == NormalizeName(dll));
            if (target is null) return null; // not a sibling: unbound here (the host resolver had its chance first)
            var label = $"{dll}!{name ?? "#" + ordinal}";
            if (target == consumer) throw new GuestImportException($"{consumer.DisplayName} imports {label} from itself.");
            if (target.Module is null) throw new GuestImportException($"{consumer.DisplayName} imports {label}, but module '{target.DisplayName}' is not loaded (dependency cycle).");

            var peExport = target.Image.Exports.FirstOrDefault(e => name is not null ? e.Name == name : e.Ordinal == ordinal);
            if (peExport is null) throw new GuestImportException($"{consumer.DisplayName} imports {label}, but '{target.DisplayName}' has no such export.");
            if (peExport.ForwarderTarget is { } fwd)
                throw new GuestImportException($"{consumer.DisplayName} imports {label}, which '{target.DisplayName}' forwards to '{fwd}'. Forwarded exports are rejected explicitly: import the forwarding target directly.");
            var export = target.Artifact.Exports.FirstOrDefault(e => e.Ordinal == peExport.Ordinal)
                ?? throw new GuestImportException($"{consumer.DisplayName} imports {label}, but the translation of '{target.DisplayName}' has no callable code for that export (see its BC_EXPORT_NOT_CODE diagnostic). Imports of DATA exports are not supported: an IAT slot would have to hold the variable's address, and only functions are bridged.");

            var signature = SignatureOf(target, export, name, consumer, label);
            dependencies.Add(target);
            var key = (target, label);
            if (!bridges.TryGetValue(key, out var bridge))
            {
                var counter = new BridgeCounter($"{consumer.DisplayName}->{target.DisplayName}!{name ?? "#" + ordinal}");
                lock (set.counters) set.counters.Add(counter);
                bridges[key] = bridge = ModuleBridge.Create(target.Module, export, signature, set.link.CdeclWindowDwords, counter);
            }
            return bridge;
        }

        private static ExportSignature SignatureOf(Entry target, TranslatedExport export, string? name, Entry consumer, string label)
        {
            var conv = target.Conventions;
            ExportSignature? explicitSig = null;
            if (name is not null && conv.Exports.TryGetValue(name, out var byName)) explicitSig = byName;
            else if (conv.Exports.TryGetValue("#" + export.Ordinal, out var byOrdinal)) explicitSig = byOrdinal;

            ExportSignature? decorated = export.Convention switch
            {
                GuestCallingConvention.Cdecl => ExportSignature.Cdecl,
                GuestCallingConvention.Stdcall when export.ArgBytes is { } b => ExportSignature.Stdcall(b / 4),
                _ => null,
            };
            var chosen = explicitSig ?? decorated ?? (conv.Default == GuestCallingConvention.Cdecl ? ExportSignature.Cdecl : null)
                ?? throw new GuestImportException($"{consumer.DisplayName} imports {label}, but the calling convention of '{target.DisplayName}'s export is not declared: it is undecorated and neither an ExportConventions entry nor a Cdecl default exists. Conventions are never assumed.");
            if (chosen.Convention == GuestCallingConvention.Stdcall && chosen.ArgCount is not >= 0)
                throw new GuestImportException($"{consumer.DisplayName} imports {label}: a stdcall export needs an exact argument count.");
            if (chosen.Convention is not (GuestCallingConvention.Cdecl or GuestCallingConvention.Stdcall))
                throw new GuestImportException($"{consumer.DisplayName} imports {label}: {chosen.Convention} exports pass arguments in registers and cannot be bridged.");
            if (explicitSig is not null && decorated is not null && (explicitSig.Convention != decorated.Convention || (decorated.ArgCount is { } d && explicitSig.ArgCount != d)))
                throw new GuestImportException($"{consumer.DisplayName} imports {label}: the declared signature ({explicitSig.Convention}, {explicitSig.ArgCount}) contradicts the export's name decoration ({decorated.Convention}, {decorated.ArgCount}).");
            return chosen;
        }
    }
}
