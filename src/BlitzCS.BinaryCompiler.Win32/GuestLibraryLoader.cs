using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Lifting;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Policy of <see cref="GuestLibraryLoader"/>.</summary>
public sealed class GuestLibraryOptions
{
    /// <summary>Target OS the DLLs are translated for (null: the host). Must be the one the executable was translated for.</summary>
    public string? TargetOs { get; init; }
    /// <summary>Translation cache: a DLL is translated on its first LoadLibrary only; later runs hit the cache. Null: translate every time.</summary>
    public string? CacheDirectory { get; init; }
    public bool AllowTranslationErrors { get; init; }
    /// <summary>Size of the dynamic stub pool given to a DLL that itself imports LoadLibrary*/GetProcAddress (still used for GetProcAddress on emulated system DLLs).</summary>
    public int DynamicStubsForLoaders { get; init; } = 256;
    /// <summary>Progress and diagnostics of the loader (not guest output).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>A guest DLL that is loaded in the process.</summary>
public sealed record GuestLibraryInfo(string Name, string WindowsPath, uint Handle, int RefCount, bool Pinned);

/// <summary>
/// Loads guest DLLs on LoadLibrary: finds the file in the sandbox like Windows would, translates it (cached), loads it into the SAME
/// <see cref="GuestModuleSet"/> as the executable with its own Win32 state, runs DllMain(DLL_PROCESS_ATTACH) and hands out the module's guest base as its HMODULE.
/// GetProcAddress on such a module returns the real guest address (module base + export RVA); calls through it are cross-module dispatch. See docs/binary-win32.md, "Guest DLLs".
/// All entry points are serialised by one lock; loading only adds new modules at new bases, so running guest code is never disturbed.
/// </summary>
public sealed class GuestLibraryLoader : IDisposable
{
    private sealed class Lib
    {
        public required string Key, HostPath, WindowsPath;
        public required PeImage Image;
        public required TranslationArtifact Artifact;
        public required uint Base, Footprint;
        public GuestModule? Module;
        public int RefCount = 1;
        public bool Pinned;
        public List<Lib> Dependencies = [];
    }

    private readonly GuestLibraryOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, Lib> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Lib> byHandle = [];
    private readonly List<(uint Start, uint End)> occupied = [];
    private readonly List<string> diagnostics = [];
    private Win32Options? win32;
    private GuestProcessInfo? process;
    private int depth;
    private readonly TranslationCache? cache;

    public GuestLibraryLoader(GuestLibraryOptions? options = null)
    {
        this.options = options ?? new GuestLibraryOptions();
        cache = this.options.CacheDirectory is { } d ? new TranslationCache(d) : null;
        // every module of the set is a module of one process: one Win32 state (heap, handles, TLS/FLS indices, critical sections ...), created from the Win32 options given to Attach
        Func<string, IGuestImportResolver>? shared = null;
        Set = new GuestModuleSet(name => (shared ??= Win32ImportResolver.SharedFactory(win32 ?? throw new InvalidOperationException("GuestLibraryLoader.Attach was not called.")))(name));
    }

    /// <summary>The set the executable and every loaded DLL live in.</summary>
    public GuestModuleSet Set { get; }

    /// <summary>Named reasons of failed loads and GetProcAddress calls (for example an undeterminable calling convention), oldest first.</summary>
    public IReadOnlyList<string> Diagnostics { get { lock (gate) return [.. diagnostics]; } }

    public IReadOnlyList<GuestLibraryInfo> Libraries { get { lock (gate) return [.. byHandle.Values.Where(l => l.Module is not null).Select(Info)]; } }

    private static GuestLibraryInfo Info(Lib l) => new(l.Key, l.WindowsPath, l.Base, l.RefCount, l.Pinned);

    /// <summary>Binds the loader to the process policy and reserves the executable's address range. Call before the executable is added to <see cref="Set"/>.</summary>
    public void Attach(Win32Options win32Options, GuestProcessInfo processInfo, PeImage mainImage)
    {
        win32 = win32Options; process = processInfo;
        lock (gate) occupied.Add((mainImage.ImageBase, mainImage.ImageBase + ModuleBaseAllocator.Footprint(mainImage)));
    }

    // ---- queries used by the kernel32 handlers ----

    internal GuestLibraryInfo? FindLoaded(Win32Call c, string name)
    {
        lock (gate)
        {
            var path = Locate(c, name);
            var lib = path is null ? null : byHandle.Values.FirstOrDefault(l => string.Equals(l.HostPath, path, StringComparison.Ordinal));
            if (lib is null && name.IndexOfAny(['\\', '/', ':']) < 0) lib = byKey.GetValueOrDefault(KeyOf(name));
            return lib is null ? null : Info(lib);
        }
    }

    internal GuestLibraryInfo? Find(uint handle) { lock (gate) return byHandle.TryGetValue(handle, out var l) ? Info(l) : null; }

    internal GuestLibraryInfo? FindByAddress(uint address)
    {
        lock (gate) return byHandle.Values.Where(l => address >= l.Base && address < l.Base + l.Footprint).Select(Info).FirstOrDefault();
    }

    /// <summary>GetModuleHandleEx on a guest DLL: default adds a reference, PIN keeps it for the life of the process, UNCHANGED_REFCOUNT does nothing.</summary>
    internal void Touch(uint handle, bool pin, bool unchanged)
    {
        lock (gate)
        {
            if (!byHandle.TryGetValue(handle, out var l)) return;
            if (pin) l.Pinned = true; else if (!unchanged) l.RefCount++;
        }
    }

    // ---- LoadLibrary / FreeLibrary ----

    internal uint Load(Win32Call c, string requested)
    {
        lock (gate)
        {
            var path = Locate(c, requested);
            if (path is null) { Note($"LoadLibrary(\"{requested}\"): not found in the sandbox."); return c.Ctx.Fail(Win32Error.ModNotFound); }
            return LoadFile(c, path, requested, isDependency: false);
        }
    }

    private uint LoadFile(Win32Call c, string hostPath, string requested, bool isDependency)
    {
        if (byHandle.Values.FirstOrDefault(l => l.HostPath == hostPath) is { } existing) { existing.RefCount++; return existing.Base; }
        var key = KeyOf(hostPath);
        if (byKey.ContainsKey(key)) { Note($"LoadLibrary(\"{requested}\"): a different module named '{key}' is already loaded; module names are unique."); return c.Ctx.Fail(Win32Error.AlreadyExists); }
        if (depth > 16) { Note($"LoadLibrary(\"{requested}\"): dependency chain too deep (cycle?)."); return c.Ctx.Fail(Win32Error.ModNotFound); }

        PeImage image;
        try { image = PeReader.Read(File.ReadAllBytes(hostPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Note($"LoadLibrary(\"{requested}\"): {e.Message}"); return c.Ctx.Fail(Win32Error.AccessDenied); }
        catch (Exception e) when (e is not OutOfMemoryException) { Note($"LoadLibrary(\"{requested}\"): not a PE image ({e.Message})."); return c.Ctx.Fail(Win32Error.BadExeFormat); }
        var bad = image.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (bad is not null || !image.IsDll)
        {
            Note($"LoadLibrary(\"{requested}\"): " + (bad is not null ? $"not a translatable PE32 image ({bad.Code}: {bad.Message})" : "the image is an executable, not a DLL") + ".");
            return c.Ctx.Fail(Win32Error.BadExeFormat);
        }

        var target = BinaryTargets.TryNormalize(options.TargetOs) ?? BinaryTargets.Host;
        var reserved = (ModuleBaseAllocator.IsWindowsTarget(target) ? ModuleBaseAllocator.WindowsReserved : ModuleBaseAllocator.DefaultReserved).Concat(occupied).ToList();
        uint guestBase, footprint;
        try { (guestBase, footprint) = Allocate(key, image, reserved, target); }
        catch (PeRebaseException e) { Note($"LoadLibrary(\"{requested}\"): {e.Message}"); return c.Ctx.Fail(Win32Error.BadExeFormat); }
        var loadImage = guestBase != image.ImageBase ? image.Rebase(guestBase) : image;

        // Dependencies first (their DllMain must have run): guest DLLs found in the sandbox; emulated and unknown DLLs are the host resolver's business.
        var lib = new Lib { Key = key, HostPath = hostPath, WindowsPath = WindowsPathOf(c, hostPath), Image = loadImage, Artifact = null!, Base = guestBase, Footprint = footprint };
        depth++;
        try
        {
            foreach (var dll in image.Imports.Select(i => i.Dll).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (c.Ctx.Process.Find(dll) is not null) continue;
                if (byKey.TryGetValue(KeyOf(dll), out var already)) { Ref(lib, already); continue; }
                if (Locate(c, dll) is not { } depPath) { Note($"{key}: imports {dll}, which is neither emulated nor in the sandbox; its functions fault by name when called."); continue; }
                var h = LoadFile(c, depPath, dll, isDependency: true);
                if (h == 0) { UnrefAll(lib); return 0; }
                lib.Dependencies.Add(byHandle[h]);
            }
        }
        finally { depth--; }

        var imports = loadImage.Imports.Select(i => i.Name).ToList();
        var needsStubs = imports.Any(n => n is "LoadLibraryA" or "LoadLibraryW" or "LoadLibraryExA" or "LoadLibraryExW" or "GetProcAddress");
        var translateOptions = new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: target, LoadBase: guestBase) { DynamicStubs = needsStubs ? options.DynamicStubsForLoaders : 0 };
        TranslationArtifact artifact;
        try
        {
            var cacheKey = CacheKey.Compute(image, translateOptions);
            if (cache is not null && cache.TryGet(cacheKey, out var hit) && hit is not null) { artifact = hit; options.Log?.Invoke($"{key}: translation cache hit"); }
            else
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                artifact = X64Translator.Translate(image, translateOptions, cacheKey);
                options.Log?.Invoke($"{key}: translated in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
                cache?.Put(cacheKey, artifact);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Note($"LoadLibrary(\"{requested}\"): translation failed ({e.GetType().Name}: {e.Message})."); UnrefAll(lib); return c.Ctx.Fail(Win32Error.BadExeFormat); }
        var errors = artifact.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        if (errors > 0) options.Log?.Invoke($"{key}: {errors} translation error(s): {string.Join("; ", artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5).Select(d => d.Code + " @0x" + d.GuestRva?.ToString("X") + " " + d.Message))}");
        if (errors > 0 && !options.AllowTranslationErrors)
        {
            Note($"LoadLibrary(\"{requested}\"): {errors} translation error(s), first: {artifact.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).Message}");
            UnrefAll(lib); return c.Ctx.Fail(Win32Error.BadExeFormat);
        }

        lib = new Lib { Key = key, HostPath = hostPath, WindowsPath = lib.WindowsPath, Image = loadImage, Artifact = artifact, Base = guestBase, Footprint = footprint, Dependencies = lib.Dependencies };
        byKey[key] = lib; byHandle[guestBase] = lib; occupied.Add((guestBase, guestBase + footprint)); // visible to GetModuleHandle/GetModuleFileName(self) from DllMain on
        try
        {
            Set.Add(key, artifact, loadImage, null, new GuestModuleOptions { AllowTranslationErrors = options.AllowTranslationErrors, AllowUnboundImports = true, Process = process });
            Set.LoadAll();
            lib.Module = Set[key];
        }
        catch (Exception e) when (e is GuestException or InvalidOperationException or ArgumentException)
        {
            Set.Discard(key);
            byKey.Remove(key); byHandle.Remove(guestBase); occupied.RemoveAll(o => o.Start == guestBase);
            UnrefAll(lib);
            Note($"LoadLibrary(\"{requested}\"): {e.GetType().Name}: {e.Message}");
            return c.Ctx.Fail(e switch { GuestInitializationException => Win32Error.DllInitFailed, GuestAddressSpaceException => Win32Error.NotEnoughMemory, _ => Win32Error.BadExeFormat });
        }
        options.Log?.Invoke($"loaded guest DLL {lib.WindowsPath} at 0x{guestBase:X8}");
        return guestBase;
    }

    private static void Ref(Lib dependent, Lib dependency) { dependency.RefCount++; dependent.Dependencies.Add(dependency); }

    private void UnrefAll(Lib lib) { foreach (var d in lib.Dependencies) Release(d); lib.Dependencies = []; }

    private (uint Base, uint Footprint) Allocate(string key, PeImage image, List<(uint Start, uint End)> reserved, string target)
    {
        var chosen = ModuleBaseAllocator.Allocate([(key, image)], reserved, target)[0];
        return (chosen.Base, ModuleBaseAllocator.Footprint(image));
    }

    internal uint Free(Win32Call c, uint handle)
    {
        lock (gate)
        {
            if (!byHandle.TryGetValue(handle, out var lib) || lib.Module is null) return c.Ctx.Fail(Win32Error.InvalidHandle);
            if (lib.RefCount <= 0) return c.Ctx.Fail(Win32Error.InvalidHandle);
            options.Log?.Invoke($"FreeLibrary({lib.Key}) refcount {lib.RefCount} -> {lib.RefCount - 1}");
            Release(lib);
            return 1;
        }
    }

    /// <summary>Drops one reference; at zero (and not pinned) runs DLL_PROCESS_DETACH and unloads. A module another loaded module still imports from stays loaded.</summary>
    private void Release(Lib lib)
    {
        if (--lib.RefCount > 0 || lib.Pinned) return;
        try { Set.Unload(lib.Key); }
        catch (InvalidOperationException e) { Note($"FreeLibrary({lib.Key}): kept loaded: {e.Message}"); lib.RefCount = 1; return; }
        byKey.Remove(lib.Key); byHandle.Remove(lib.Base); occupied.RemoveAll(o => o.Start == lib.Base);
        foreach (var d in lib.Dependencies) Release(d);
    }

    // ---- GetProcAddress ----

    /// <summary>Resolves an export of a guest DLL (by name when <paramref name="name"/> is not null, else by ordinal). Returns 0 with the last error set and a diagnostic on failure.</summary>
    internal uint ProcAddress(Win32Call c, uint handle, string? name, ushort ordinal) 
    {
        lock (gate)
        {
            if (!byHandle.TryGetValue(handle, out var lib) || lib.Module is null) return c.Ctx.Fail(Win32Error.ModNotFound);
            return ResolveExport(c, lib, name, ordinal, 0);
        }
    }

    private uint ResolveExport(Win32Call c, Lib lib, string? name, ushort ordinal, int hops)
    {
        var label = $"{lib.Key}!{name ?? "#" + ordinal}";
        var pe = lib.Image.Exports.FirstOrDefault(e => name is not null ? string.Equals(e.Name, name, StringComparison.Ordinal) : e.Ordinal == ordinal);
        if (pe is null) { Note($"GetProcAddress: {lib.Key} has no export '{name ?? "#" + ordinal}'."); return c.Ctx.Fail(Win32Error.ProcNotFound); }
        if (pe.ForwarderTarget is { } fwd) return ResolveForwarder(c, label, fwd, hops);
        var te = lib.Artifact.Exports.FirstOrDefault(e => e.Ordinal == pe.Ordinal);
        if (te is null)
        {
            if (lib.Image.Sections.Any(s => s.IsExecutable && pe.Rva >= s.VirtualAddress && pe.Rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize)))
            {
                Note($"GetProcAddress: {label} points at executable bytes the translator did not turn into code (see the BC_EXPORT_NOT_CODE diagnostic).");
                return c.Ctx.Fail(Win32Error.ProcNotFound);
            }
            return lib.Base + pe.Rva; // a data export: the variable's address, no stub
        }

        // The real address, as on Windows (base + export RVA): callers do arithmetic with it, and a call through it reaches the export's own translated code
        // through cross-module dispatch, which needs no calling-convention knowledge because the callee pops its own arguments.
        options.Log?.Invoke($"GetProcAddress({label}) -> 0x{lib.Base + pe.Rva:X8}");
        return lib.Base + pe.Rva;
    }

    private uint ResolveForwarder(Win32Call c, string label, string forwarder, int hops)
    {
        var dot = forwarder.LastIndexOf('.');
        if (hops > 8 || dot <= 0) { Note($"GetProcAddress: {label} forwards to '{forwarder}', which cannot be followed."); return c.Ctx.Fail(Win32Error.ProcNotFound); }
        var dll = forwarder[..dot]; var symbol = forwarder[(dot + 1)..];
        string? name = symbol.StartsWith('#') ? null : symbol;
        ushort ord = 0;
        if (name is null && !ushort.TryParse(symbol.AsSpan(1), out ord)) return c.Ctx.Fail(Win32Error.ProcNotFound);
        if (byKey.TryGetValue(KeyOf(dll), out var target) && target.Module is not null) return ResolveExport(c, target, name, ord, hops + 1);
        if (name is not null && c.Ctx.Process.Find(dll) is { } sys && c.Ctx.Process.IsSystem(sys))
        {
            if (c.Ctx.Process.ExportStubAddress?.Invoke(sys.Name, name) is { } s) return s;
            if (c.Ctx.ApiLookup?.Invoke(sys.Name, name) is { } api && GuestModule.Current is { StubCapacity: > 0 } m) return c.Ctx.Process.StubFor(m, api);
        }
        Note($"GetProcAddress: {label} forwards to '{forwarder}', which is not a loaded guest DLL or an implemented API.");
        return c.Ctx.Fail(Win32Error.ProcNotFound);
    }

    // ---- file location ----

    private static string KeyOf(string nameOrPath) => GuestModuleSet.NormalizeName(nameOrPath);

    /// <summary>
    /// Finds the DLL file in the sandbox. A name with a directory is resolved as given (and ".dll" appended when it has no extension); a bare name is searched in the
    /// directory of the executable, the sandbox root and the process's current directory, in that order.
    /// </summary>
    private static string? Locate(Win32Call c, string name)
    {
        var candidates = new List<string>();
        var named = Path.HasExtension(name.Replace('/', '\\').Split('\\')[^1]) ? name : name + ".dll";
        if (name.IndexOfAny(['\\', '/']) >= 0 || name.Length >= 2 && name[1] == ':') candidates.Add(named);
        else
        {
            var exeDir = c.Ctx.Options.MainModulePath.Replace('/', '\\');
            var cut = exeDir.LastIndexOf('\\');
            candidates.Add((cut >= 0 ? exeDir[..(cut + 1)] : "\\") + named);
            candidates.Add("\\" + named);
            candidates.Add(c.Ctx.Process.CurrentDirectory.TrimEnd('\\') + "\\" + named);
        }
        foreach (var candidate in candidates)
        {
            var (host, error) = c.Ctx.Files.Translate(candidate);
            if (error == Win32Error.Success && host is not null && File.Exists(host)) return Path.GetFullPath(host);
        }
        return null;
    }

    private string WindowsPathOf(Win32Call c, string hostPath)
    {
        var root = c.Ctx.Options.SandboxRoot;
        if (root is null) return hostPath;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), hostPath);
        return "C:\\" + relative.Replace('/', '\\');
    }

    private void Note(string message)
    {
        diagnostics.Add(message);
        options.Log?.Invoke(message);
        win32?.DebugOutput?.Invoke(message);
    }

    public void Dispose() => Set.Dispose();
}
