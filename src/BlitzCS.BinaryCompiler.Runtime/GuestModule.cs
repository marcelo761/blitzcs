using System.Runtime.InteropServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>
/// A translated PE32 image loaded into this process (Linux x86-64 SysV and Windows x86-64; other platforms throw
/// <see cref="PlatformNotSupportedException"/>). Lifetime follows NativeLibraryOwner: explicit disposal; calls hold a
/// shared lock and Dispose takes the exclusive lock, so it waits for in-flight calls.
/// </summary>
/// <remarks>
/// Memory: the guest image is mapped at exactly artifact.GuestImageBase (MAP_FIXED_NOREPLACE, must be below 4 GiB). If
/// another mapping occupies the range, loading fails with <see cref="GuestAddressSpaceException"/>; relocating is not
/// possible because the translated code embeds absolute guest addresses. Pages are RW while populated, then set to
/// RX/RW/R per section (a page needing both W and X is rejected: no RWX mapping ever exists). The host image is copied
/// while RW, then its code part is switched to RX.
/// Concurrency: the artifact's GuestContext holds a single saved native RSP, so guest execution is serialized by a
/// per-module lock (re-entrant for guest->import->managed->guest on the same thread). See the report for the contract
/// change that would allow parallel calls.
/// </remarks>
public sealed unsafe partial class GuestModule : IDisposable
{
    private const uint CanaryWord = 0xCAFEBABE;

    /// <summary>Internal capture mode of the cross-module bridge: like St0, but a callee that leaves the x87 stack empty is not an error (the result then reports Kind == Eax).</summary>
    internal const GuestReturn St0IfPresent = (GuestReturn)3;

    private int dependents;

    /// <summary>True when allocations made for the whole process (shared Win32 state) live in this module; see <see cref="GuestMemory.PinAsProcessAnchor"/>.</summary>
    internal volatile bool IsProcessAnchor;

    /// <summary>Number of modules (in a <see cref="GuestModuleSet"/>) whose imports are bound to this module's exports.</summary>
    internal int DependentCount => Volatile.Read(ref dependents);
    internal void AddDependent() => Interlocked.Increment(ref dependents);
    internal void RemoveDependent() => Interlocked.Decrement(ref dependents);

    [ThreadStatic] private static GuestModule? current;

    private sealed class ThreadState
    {
        public int Holds, Depth;
        public uint ImportEsp;
        public Exception? PendingFault;
        public unsafe byte* CallContext;
        public GuestProcessEnvironment.TebBlock? Teb;
        public uint ExtFlags, ExtEdx;           // extended import return staged by the running handler (GuestCall.SetReturnEdx / SetReturnSt0)
        public readonly byte[] ExtSt0 = new byte[10];
    }

    private sealed class GuestStack
    {
        public nint Map; public nuint MapLength; public uint Low, Top;
    }

    private readonly TranslationArtifact artifact;
    private readonly GuestModuleOptions options;
    private readonly ReaderWriterLockSlim lifetime = new(LockRecursionPolicy.SupportsRecursion);
    private readonly object resourceLock = new();
    private readonly ThreadLocal<ThreadState> threadState = new(() => new ThreadState());
    private readonly List<GuestStack> allStacks = [];
    private readonly Stack<GuestStack> idleStacks = new();
    private readonly Dictionary<GuestBuffer, (nint Map, nuint Length)> buffers = [];
    private uint entryRva, imageEntryRva;
    private bool imageIsDll;
    private GuestImport[] boundImports = [];
    private nint guestMap, hostMap;
    private nuint guestMapLength, hostMapLength;
    private bool attached, disposed;

    private GuestModule(TranslationArtifact artifact, GuestModuleOptions options, GuestProcessEnvironment environment, string name)
    {
        this.artifact = artifact;
        this.options = options;
        env = environment;
        Name = name;
        Memory = new GuestMemory(this);
    }

    /// <summary>The name the module was registered under in its set (the image base in hex for a module loaded on its own); used in diagnostics.</summary>
    public string Name { get; }


    /// <summary>Disassembles the guest instruction at <paramref name="va"/> for a trap message; "an instruction" when the bytes are not readable.</summary>
    private unsafe string DescribeGuestInstruction(uint va)
    {
        try
        {
            var bytes = new byte[15];
            for (var k = 0; k < 15; k++) bytes[k] = *(byte*)(nuint)(va + (uint)k);
            var decoder = Iced.Intel.Decoder.Create(32, bytes, va);
            var ins = decoder.Decode();
            if (ins.IsInvalid) return "an instruction";
            var fmt = new Iced.Intel.NasmFormatter();
            var output = new Iced.Intel.StringOutput();
            fmt.Format(ins, output);
            return "'" + output.ToStringAndReset() + "'";
        }
        catch (Exception) { return "an instruction"; }
    }

    public GuestMemory Memory { get; }
    public uint ImageBase => artifact.GuestImageBase;
    public nint HostImageBase => hostMap;
    public bool IsDisposed => Volatile.Read(ref disposed);

    /// <summary>Bound imports in IAT order with the guest-callable address of each one's stub (the value stored in its IAT slot).</summary>
    public IReadOnlyList<(string Dll, string Name, uint StubAddress)> BoundImports =>
        [.. artifact.Imports.Select((imp, i) => (imp.Dll, imp.Name ?? "#" + imp.Ordinal, artifact.GuestImageBase + artifact.ImportStubBaseRva + 16u * (uint)i))];

    /// <summary>Guest-callable stub address of a bound import (DLL compared case-insensitively; name or "#ordinal"), or null. For Win32Context.SetExportStubAddressProvider.</summary>
    public uint? GetImportStubAddress(string dll, string name)
    {
        for (var i = 0; i < artifact.Imports.Count; i++)
        {
            var imp = artifact.Imports[i];
            if (string.Equals(imp.Dll, dll, StringComparison.OrdinalIgnoreCase) && (imp.Name ?? "#" + imp.Ordinal) == name)
                return artifact.GuestImageBase + artifact.ImportStubBaseRva + 16u * (uint)i;
        }
        return null;
    }

    /// <summary>The most recent exception thrown by a managed import (kept after the owning call has thrown).</summary>
    public Exception? LastImportFault { get; private set; }

    /// <summary>Set if DLL_PROCESS_DETACH failed during Dispose (Dispose itself does not throw for guest failures).</summary>
    public Exception? DetachError { get; private set; }

    /// <summary>The module whose guest code is running on this thread, for imports that need memory access.</summary>
    public static GuestModule? Current => current;

    public static GuestModule Load(TranslationArtifact artifact, PeImage image, IGuestImportResolver imports, GuestModuleOptions? options = null) =>
        Load(artifact, image, imports, options, null, null);

    internal static GuestModule Load(TranslationArtifact artifact, PeImage image, IGuestImportResolver imports, GuestModuleOptions? options, GuestProcessEnvironment? environment, string? name)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imports);
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The guest runtime supports only Linux x86-64 (SysV) and Windows x86-64; loading translated PE images is not available on this platform.");

        options ??= new GuestModuleOptions();
        Validate(artifact, image, options);
        var module = new GuestModule(artifact, options, environment ?? new GuestProcessEnvironment(ownedBySet: false), name ?? $"0x{artifact.GuestImageBase:X8}");
        module.env.Attach(module);
        try { module.Initialize(image, imports); }
        catch { module.Teardown(); throw; }
        return module;
    }

    private static void Validate(TranslationArtifact a, PeImage image, GuestModuleOptions options)
    {
        var host = PlatformMemory.Current;
        if (a.FormatVersion < ArtifactSerializer.CurrentVersion)
            throw new GuestLoadException($"The artifact has format version {a.FormatVersion}; this runtime needs {ArtifactSerializer.CurrentVersion} (cross-module dispatch: the translated code must reach the module registry). Re-translate the image.");
        var artifactTarget = a.TargetOs ?? BinaryTargets.LinuxSysV;
        if (!string.Equals(artifactTarget, host.TargetOs, StringComparison.Ordinal))
            throw new GuestLoadException($"The artifact was translated for target '{artifactTarget}' but this host is '{host.TargetOs}'. Artifacts are target specific (their runtime routines follow the host calling convention): re-translate with TargetOs = \"{host.TargetOs}\".");
        var errors = a.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0 && !options.AllowTranslationErrors)
            throw new GuestLoadException("The translation artifact contains error diagnostics and cannot be loaded: " +
                string.Join("; ", errors.Take(5).Select(d => $"{d.Code}: {d.Message}")) + (errors.Count > 5 ? $"; (+{errors.Count - 5} more)" : "") + ".");
        if (image.Tls is not null && !a.Diagnostics.Any(d => d.Code == "TLS_HANDLED"))
            throw new GuestUnsupportedException("The image has a TLS directory but the artifact was not produced by a compiler that models it (no TLS_HANDLED diagnostic); re-translate the image.");
        if (!string.Equals(a.SourceSha256, image.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new GuestLoadException("The translation artifact was produced from a different image (SHA-256 mismatch).");
        if (a.RebasedFromImageBase != image.RebasedFrom?.OriginalImageBase)
            throw new GuestLoadException(a.RebasedFromImageBase is { } from
                ? $"The artifact was translated for a copy rebased from 0x{from:X8}; pass the image returned by PeImage.Rebase(0x{a.GuestImageBase:X8})."
                : "The image was rebased but the artifact was translated at the image's original base.");
        if (a.GuestImageBase != image.ImageBase || a.GuestImageSize != image.SizeOfImage)
            throw new GuestLoadException("The artifact's guest image base/size does not match the PE image.");
        if (a.Imports.Count != image.Imports.Count || !a.Imports.Zip(image.Imports).All(p => p.First.IatRva == p.Second.IatRva && p.First.Dll == p.Second.Dll))
            throw new GuestLoadException("The artifact's import list does not match the PE image.");
        var ps = (uint)PlatformMemory.Current.PageSize;
        if (a.GuestImageBase < host.MinimumImageBase || a.GuestImageBase % (uint)host.AllocationGranularity != 0)
            throw new GuestAddressSpaceException($"Guest image base 0x{a.GuestImageBase:X8} is not usable on {host.Name}: bases must be at least 0x{host.MinimumImageBase:X8} and aligned to 0x{host.AllocationGranularity:X}." +
                (host.Name == "windows" ? " Re-translate the library rebased (BinaryCompilerOptions.LoadBase / ModuleBaseAllocator with TargetOs windows-x64); images without base relocations cannot be moved." : ""));
        if (a.GuestImageBase == 0 || a.GuestImageBase % ps != 0 || (ulong)a.GuestImageBase + a.GuestImageSize > uint.MaxValue)
            throw new GuestAddressSpaceException($"Guest image base 0x{a.GuestImageBase:X8} (size 0x{a.GuestImageSize:X}) is not page aligned or does not fit below 4 GiB.");
        if (a.CodeSize <= 0 || a.CodeSize % ps != 0 || a.CodeSize >= a.HostImage.Length)
            throw new GuestLoadException($"HostImage code size {a.CodeSize} must be a positive multiple of the page size and smaller than the image ({a.HostImage.Length}).");
        var len = (long)a.HostImage.Length;
        if (a.EnterGuestOffset >= a.CodeSize) throw new GuestLoadException("EnterGuestOffset lies outside the code area.");
        if (a.ContextOffset < a.CodeSize || a.ContextOffset + 64L > len) throw new GuestLoadException("ContextOffset must lie inside the RW data area.");
        if (a.ImportThunkTableOffset < a.CodeSize || a.ImportThunkTableOffset + 8L * (a.Imports.Count + a.DynamicStubs) > len)
            throw new GuestLoadException("ImportThunkTableOffset must lie inside the RW data area.");
        if (a.ExitGuestRva < a.GuestImageSize || a.ImportStubBaseRva < a.GuestImageSize)
            throw new GuestLoadException("ExitGuestRva and ImportStubBaseRva must be synthetic RVAs at or above SizeOfImage.");
        foreach (var imp in a.Imports)
            if ((ulong)imp.IatRva + 4 > a.GuestImageSize) throw new GuestLoadException($"IAT slot RVA 0x{imp.IatRva:X} of {imp.Dll} lies outside the image.");
        foreach (var e in a.Exports)
            if (e.HostOffset >= a.CodeSize) throw new GuestLoadException($"Export '{e.Name}' host offset lies outside the code area.");
    }

    private void Initialize(PeImage image, IGuestImportResolver resolver)
    {
        // 1. Bind every import first: an unbound import is a load-time failure, with no stubs and no fake success.
        var bound = new GuestImport[artifact.Imports.Count];
        var missing = new List<string>();
        var dataImports = new GuestDataImport?[bound.Length];
        for (var i = 0; i < bound.Length; i++)
        {
            var imp = artifact.Imports[i];
            var label = $"{imp.Dll}!{imp.Name ?? "#" + imp.Ordinal}";
            if (resolver is IGuestDataImportResolver dataResolver && dataResolver.ResolveData(imp.Dll, imp.Name, imp.Ordinal) is { } data)
            {
                dataImports[i] = data; // the IAT slot gets the variable's address below; calling through it must still fail loudly
                bound[i] = GuestImport.Create(_ => throw new GuestImportException($"The guest called data import {label} as a function."), 0);
                continue;
            }
            var resolved = resolver.Resolve(imp.Dll, imp.Name, imp.Ordinal);
            if (resolved is not null) bound[i] = resolved;
            else if (options.AllowUnboundImports)
                bound[i] = GuestImport.Create(_ => throw new GuestImportException($"The guest called unbound import {label}, which no host service provides."), 0);
            else missing.Add(label);
        }
        if (missing.Count > 0)
            throw new GuestImportException("Unbound import(s): " + string.Join(", ", missing) + ".");
        boundImports = bound;

        // 2. Guest image at the fixed base.
        var ps = (uint)PlatformMemory.Current.PageSize;
        var end = Math.Max(artifact.GuestImageSize, Math.Max(artifact.ExitGuestRva + 1UL, artifact.ImportStubBaseRva + 16UL * (uint)Math.Max(1, bound.Length + artifact.DynamicStubs)));
        var size = (end + ps - 1) / ps * ps;
        if ((ulong)artifact.GuestImageBase + size > uint.MaxValue)
            throw new GuestAddressSpaceException("Guest image plus synthetic region does not fit below 4 GiB.");
        guestMapLength = (nuint)size;
        var addr = PlatformMemory.Current.MapFixed((nint)artifact.GuestImageBase, guestMapLength, out var errno);
        if (addr == 0)
        {
            guestMapLength = 0;
            throw new GuestAddressSpaceException($"Cannot map the guest image at its fixed base 0x{artifact.GuestImageBase:X8} (0x{size:X} bytes, OS error {errno}); the range is occupied by {AddressSpace.DescribeConflicts(artifact.GuestImageBase, size)}. Translated code is bound to this base: re-translate for another base (BinaryCompilerOptions.LoadBase / ModuleBaseAllocator).");
        }
        guestMap = addr;

        var mapped = image.MapImage();
        Marshal.Copy(mapped, 0, guestMap, mapped.Length);
        AllocatePeb(image);
        InitializeTls(image, mapped);
        for (var i = 0; i < bound.Length; i++)
            *(uint*)(guestMap + (nint)artifact.Imports[i].IatRva) = artifact.GuestImageBase + artifact.ImportStubBaseRva + 16u * (uint)i;
        for (var i = 0; i < bound.Length; i++)
            if (dataImports[i] is { } variable)
            {
                var address = variable.Materialize(Memory);
                // written before ProtectGuest makes the IAT read-only
                *(uint*)(guestMap + (nint)artifact.Imports[i].IatRva) = address;
            }

        ProtectGuest(image, (int)(size / ps));
        // 3. Host image: copy while RW, then flip the code part to RX.
        var hostLen = ((nuint)artifact.HostImage.Length + ps - 1) / ps * ps;
        hostMapLength = hostLen;
        hostMap = PlatformMemory.Current.Map(hostLen, out errno);
        if (hostMap == 0) { hostMapLength = 0; throw new GuestLoadException($"Cannot map the host image (OS error {errno})."); }
        Marshal.Copy(artifact.HostImage, 0, hostMap, artifact.HostImage.Length);
        for (var i = 0; i < bound.Length; i++)
            *(nint*)(hostMap + (nint)artifact.ImportThunkTableOffset + 8 * i) = bound[i].FunctionPointer;
        InitializeStubPool();
        // Cross-module dispatch: the registry cell is filled and the module published before any guest code (TLS callbacks, DllMain) runs, because DllMain may hand
        // its own function pointers to modules that call them while it is still running.
        if (artifact.ContextOffset + ModuleRegistryLayout.CellOffset + 8UL > (ulong)artifact.HostImage.Length) throw new GuestLoadException("ContextOffset leaves no room for the module registry cell.");
        *(nint*)(hostMap + (nint)(artifact.ContextOffset + ModuleRegistryLayout.CellOffset)) = env.RegistryAddress;
        var tableEntries = (uint)((artifact.HostImage.Length - (long)artifact.DispatchTableOffset) / 4);
        registryBound = tableEntries - 1;
        registrySlot = env.Publish(artifact.GuestImageBase, registryBound, hostMap, hostMap + (nint)artifact.DispatchTableOffset);
        PlatformMemory.Current.Protect(hostMap, (nuint)artifact.CodeSize, MemoryProtection.ReadExecute);

        imageEntryRva = image.EntryPointRva; imageIsDll = image.IsDll;
        // 4. TLS callbacks (DLL_PROCESS_ATTACH), then DllMain(hinst, DLL_PROCESS_ATTACH, 0)
        if (options.RunEntryPoint && tlsCallbacks.Length > 0)
        {
            try { RunTlsCallbacks(DllProcessAttach); }
            catch (GuestException e) { throw new GuestInitializationException($"TLS callback(DLL_PROCESS_ATTACH) failed: {e.Message}", e); }
            tlsProcessAttached = true;
        }
        if (options.RunEntryPoint && image.IsDll && image.EntryPointRva != 0)
        {
            var target = ResolveEntry(image.EntryPointRva);
            uint result;
            try { result = InvokeCore(target, [artifact.GuestImageBase, 1u, 0u]); }
            catch (GuestException e) { throw new GuestInitializationException($"DllMain(DLL_PROCESS_ATTACH) failed: {e.Message}", e); }
            if (result == 0) throw new GuestInitializationException("DllMain(DLL_PROCESS_ATTACH) returned FALSE.");
            entryRva = image.EntryPointRva;
            attached = true;
        }
        initializing = false;
    }

    private nint ResolveEntry(uint rva)
    {
        foreach (var m in artifact.AddressMap)
            if (m.GuestRva == rva) return hostMap + (nint)m.HostOffset;
        foreach (var e in artifact.Exports)
            if (e.GuestRva == rva) return hostMap + (nint)e.HostOffset;
        throw new GuestInitializationException($"The PE entry point RVA 0x{rva:X} was not translated.");
    }

    private void ProtectGuest(PeImage image, int pageCount)
    {
        var platform = PlatformMemory.Current;
        var ps = (ulong)platform.PageSize;
        const int R = 1, W = 2, X = 4;
        var prot = new int[pageCount];
        Array.Fill(prot, R);
        foreach (var s in image.Sections)
        {
            var span = s.VirtualSize != 0 ? s.VirtualSize : s.RawSize;
            if (span == 0) continue;
            var flags = R | (s.IsWritable ? W : 0) | (s.IsExecutable ? X : 0);
            var first = (int)(s.VirtualAddress / ps);
            var last = (int)(((ulong)s.VirtualAddress + span - 1) / ps);
            for (var p = first; p <= last && p < pageCount; p++)
            {
                prot[p] |= flags;
                if ((prot[p] & (W | X)) == (W | X))
                    throw new GuestLoadException($"Section '{s.Name}' would make guest page 0x{(ulong)p * ps:X} both writable and executable; W^X violations are rejected.");
            }
        }
        for (var p = 0; p < pageCount;)
        {
            var q = p;
            while (q + 1 < pageCount && prot[q + 1] == prot[p]) q++;
            platform.Protect(guestMap + (nint)((ulong)p * ps), (nuint)((ulong)(q - p + 1) * ps),
                (prot[p] & X) != 0 ? MemoryProtection.ReadExecute : (prot[p] & W) != 0 ? MemoryProtection.ReadWrite : MemoryProtection.Read);
            p = q + 1;
        }
        var writable = new bool[pageCount];
        var executable = new bool[pageCount];
        for (var p = 0; p < pageCount; p++) { writable[p] = (prot[p] & W) != 0; executable[p] = (prot[p] & X) != 0; }
        Memory.Add(new GuestRegion((ulong)guestMap, (ulong)guestMap + guestMapLength, writable) { Kind = GuestPageKind.ImageData, PageExecutable = executable, Owner = $"translated module at base 0x{artifact.GuestImageBase:X8} (SHA-256 {image.Sha256[..Math.Min(12, image.Sha256.Length)]})" });
    }

    /// <summary>Looks an export up by name. Convention comes from the caller, else from the artifact; Unknown is never guessed.</summary>
    public GuestFunction Bind(string exportName, GuestCallingConvention convention, int argCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        var export = artifact.Exports.FirstOrDefault(e => e.Name == exportName)
            ?? throw new GuestExportException($"The translated image has no export named '{exportName}'.");
        return BindCore(export, exportName, convention, argCount);
    }

    public GuestFunction Bind(ushort ordinal, GuestCallingConvention convention, int argCount)
    {
        var export = artifact.Exports.FirstOrDefault(e => e.Ordinal == ordinal)
            ?? throw new GuestExportException($"The translated image has no export with ordinal {ordinal}.");
        return BindCore(export, export.Name ?? "#" + ordinal, convention, argCount);
    }

    private GuestFunction BindCore(TranslatedExport export, string label, GuestCallingConvention convention, int argCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(argCount);
        EnterCall();
        try
        {
            if (convention != GuestCallingConvention.Unknown && export.Convention != GuestCallingConvention.Unknown && convention != export.Convention)
                throw new ArgumentException($"Export '{label}' is {export.Convention} according to the translation, but {convention} was requested.", nameof(convention));
            var effective = convention != GuestCallingConvention.Unknown ? convention : export.Convention;
            if (effective == GuestCallingConvention.Unknown)
                throw new ArgumentException($"The calling convention of export '{label}' is unknown; supply Cdecl or Stdcall explicitly.", nameof(convention));
            if (effective == GuestCallingConvention.Thiscall && argCount < 1)
                throw new ArgumentOutOfRangeException(nameof(argCount), argCount, $"Thiscall export '{label}' needs at least the 'this' argument (ECX); argCount counts it.");
            // Stdcall decoration _f@N and MSVC fastcall decoration @f@N both give the total size of the parameters (register-passed ones included).
            if (effective is GuestCallingConvention.Stdcall or GuestCallingConvention.Fastcall && export.ArgBytes is { } bytes && bytes != 4 * argCount)
                throw new ArgumentException($"Export '{label}' takes {bytes} byte(s) of arguments but {argCount} argument(s) ({4 * argCount} bytes) were declared.", nameof(argCount));
            return new GuestFunction(this, label, hostMap + (nint)export.HostOffset, effective, argCount);
        }
        finally { ExitCall(); }
    }

    // ---- call machinery ----

    internal uint Invoke(nint hostTarget, ReadOnlySpan<uint> args, uint ecx = 0, uint edx = 0)
    {
        EnterCall();
        try { return InvokeCore(hostTarget, args, GuestReturn.Eax, null, ecx, edx); }
        finally { ExitCall(); }
    }

    /// <summary>Calls the guest and captures the return registers selected by <paramref name="kind"/> (EAX and EDX are always reported).</summary>
    internal GuestCallResult Invoke(nint hostTarget, ReadOnlySpan<uint> args, GuestReturn kind, uint ecx = 0, uint edx = 0)
    {
        EnterCall();
        try
        {
            GuestCallResult result = default;
            InvokeCore(hostTarget, args, kind, &result, ecx, edx);
            return result;
        }
        finally { ExitCall(); }
    }

    private uint InvokeCore(nint hostTarget, ReadOnlySpan<uint> args, GuestReturn kind = GuestReturn.Eax, GuestCallResult* result = null, uint ecx = 0, uint edx = 0)
    {
        {
            var state = threadState.Value!;
            if (state.Teb is null) CreateThreadTeb(state);
            var teb = state.Teb!;
            var outermost = state.Depth == 0;
            var processOutermost = teb.Depth == 0;   // no guest code of any module of the process runs on this thread
            uint savedStackBase = 0, savedStackLimit = 0;
            GuestStack stack;
            uint start;
            var savedEsp = state.ImportEsp;
            if (outermost)
            {
                stack = RentStack();
                start = stack.Top;
                state.PendingFault = null;
            }
            else
            {
                start = state.ImportEsp;
                stack = FindStack(start);
            }
            if (processOutermost) GuestProcessEnvironment.PrepareTebForCall(teb, stack.Top, stack.Low);
            else if (outermost)
            {
                // A nested call into another module of the process runs on that module's own stack: the shared TEB follows it and is restored afterwards. The SEH chain is left alone.
                savedStackBase = *(uint*)(teb.Map + (nint)TebStackBase); savedStackLimit = *(uint*)(teb.Map + (nint)TebStackLimit);
                *(uint*)(teb.Map + (nint)TebStackBase) = stack.Top; *(uint*)(teb.Map + (nint)TebStackLimit) = stack.Low;
            }

            var previous = current;
            try
            {
                var canaryTop = start & ~15u;
                var argStart = (canaryTop - 16 - 4u * (uint)args.Length) & ~15u;
                var esp = argStart - 4;
                if (start < stack.Low || esp < stack.Low + (uint)options.StackReserve)
                    throw new GuestStackException($"Guest stack budget exceeded: a frame at 0x{esp:X8} would leave less than {options.StackReserve} byte(s) above the stack limit 0x{stack.Low:X8}.");

                var frame = (uint*)(nuint)esp;
                frame[0] = artifact.GuestImageBase + artifact.ExitGuestRva;
                for (var i = 0; i < args.Length; i++) frame[1 + i] = args[i];
                var canaryStart = argStart + 4u * (uint)args.Length;
                if (options.CheckStackCanary)
                    for (var a = canaryStart; a < canaryTop; a += 4) *(uint*)(nuint)a = CanaryWord;

                var enter = (delegate* unmanaged<ulong, uint, ulong, ulong>)(hostMap + (nint)artifact.EnterGuestOffset);
                // Call context (layout: CodeGen/X64CodeGen.cs, Ctx* constants): caller-owned, 128 bytes, zeroed.
                byte* callContext = stackalloc byte[128];
                new Span<byte>(callContext, 128).Clear();
                *(ulong*)(callContext + 40) = teb.Address;
                *(uint*)(callContext + 8) = ecx; // CtxEcxIn / CtxEdxIn: register arguments of thiscall and fastcall
                *(uint*)(callContext + 12) = edx;
                *(uint*)(callContext + 56) = kind switch { GuestReturn.St0 => 2u, GuestReturn.Xmm0 => 4u, St0IfPresent => 2u, _ => 0u };
                current = this;
                state.Depth++;
                if (teb.Depth++ == 0) env.ThreadEntered();
                var savedContext = state.CallContext;
                state.CallContext = callContext;
                uint eax;
                try { eax = (uint)enter((ulong)hostTarget, esp, (ulong)callContext); }
                finally
                {
                    state.Depth--; current = previous; state.ImportEsp = savedEsp; state.CallContext = savedContext;
                    if (--teb.Depth == 0) env.ThreadLeft();
                    else if (outermost) { *(uint*)(teb.Map + (nint)TebStackBase) = savedStackBase; *(uint*)(teb.Map + (nint)TebStackLimit) = savedStackLimit; }
                }

                var abi = *(uint*)(callContext + 112);
                if ((abi & 1) != 0)
                    throw new GuestAbiException("The x87 FPU stack was not empty when the guest call started (a previous translated routine or host code left a value on it); the guest was not run.");
                var trapKind = *(uint*)(callContext + 16);
                if (trapKind != 0)
                {
                    var trapAddress = *(ulong*)(callContext + 24);
                    if (trapKind == 4) throw new GuestFastFailException(*(uint*)(callContext + 8), (uint)trapAddress);
                    if (trapKind == 5) throw new GuestTrapException($"Guest executed {DescribeGuestInstruction((uint)trapAddress)} at 0x{trapAddress:X8}, a segment-register or far-pointer load whose new value is not the current flat selector (the guest runs in a flat model; this is a named deferred fault, not a translation error).");
                    throw new GuestTrapException(trapKind == 1
                        ? env.FindRetired((uint)trapAddress) is { } gone
                            ? $"Guest control flow reached 0x{trapAddress:X8}, which lies inside module '{gone}' that has been unloaded (FreeLibrary/Unload): a function pointer or return address into it outlived the module."
                            : $"Guest control flow reached untranslated code at 0x{trapAddress:X8} (indirect target not discovered at build time, and not inside any other module of this process)."
                        : trapKind == 3
                            ? $"Guest instruction at 0x{trapAddress:X8} used an fs: offset outside the thread environment block (run-time offset beyond the TLS slot array)."
                            : $"Guest executed an instruction the translator rejected at 0x{trapAddress:X8}.");
                }
                var fault = state.PendingFault;
                if (fault is not null)
                {
                    if (outermost) state.PendingFault = null;
                    throw new GuestImportException($"A managed import failed during the guest call: {fault.GetType().Name}: {fault.Message}", fault);
                }
                if (options.CheckStackCanary)
                    for (var a = canaryStart; a < canaryTop; a += 4)
                        if (*(uint*)(nuint)a != CanaryWord)
                            throw new GuestStackException($"Guest stack corruption: the call frame canary at 0x{a:X8} was overwritten (the callee wrote above its declared {args.Length} argument(s)).");
                if ((abi & 4) != 0 && kind != St0IfPresent)
                    throw new GuestAbiException("A floating-point return in ST0 was requested but the guest returned with an empty x87 stack.");
                if ((abi & 2) != 0)
                    throw new GuestAbiException(kind == GuestReturn.St0
                        ? "The guest returned with extra values on the x87 stack (the stack was reset)."
                        : "The guest returned with values left on the x87 stack (for example a float/double result in ST0 that this call did not request; the stack was reset). Use the typed call (GuestFunction.CallDouble / CallSingle / Invoke(GuestReturn.St0)).");
                if (result is not null)
                    *result = new GuestCallResult(eax, *(uint*)(callContext + 48), kind == St0IfPresent ? ((abi & 4) != 0 ? GuestReturn.Eax : GuestReturn.St0) : kind,
                        *(ulong*)(callContext + 64), *(ushort*)(callContext + 72), *(double*)(callContext + 80), *(float*)(callContext + 88),
                        *(ulong*)(callContext + 96), *(ulong*)(callContext + 104));
                return eax;
            }
            finally
            {
                if (outermost) ReturnStack(stack);
            }
        }
    }

    internal static void StageImportEdx(uint value)
    {
        var state = (current ?? throw new InvalidOperationException("SetReturnEdx may only be called from inside a guest import handler.")).threadState.Value!;
        state.ExtEdx = value; state.ExtFlags |= 1;
    }

    internal static void StageImportSt0(ReadOnlySpan<byte> extended80)
    {
        if (extended80.Length != 10) throw new ArgumentException("An x87 extended-precision image is exactly 10 bytes.", nameof(extended80));
        var state = (current ?? throw new InvalidOperationException("SetReturnSt0 may only be called from inside a guest import handler.")).threadState.Value!;
        extended80.CopyTo(state.ExtSt0);
        state.ExtFlags |= 2;
    }

    /// <summary>Entry point of every managed import thunk. Never throws: faults are recorded and surface from the owning Call.</summary>
    internal static ulong DispatchImport(GuestImportHandler handler, uint popBytes, uint esp)
    {
        var pop = (ulong)popBytes << 32;
        var module = current;
        if (module is null) return pop; // no owning call on this thread; nothing can observe a fault
        try
        {
            var state = module.threadState.Value!;
            if (state.PendingFault is not null) return pop; // after a fault the rest of the call is a no-op
            state.ImportEsp = esp;
            state.ExtFlags = 0;
            var eax = handler(new GuestCall(module.Memory, esp)) | pop;
            if (state.ExtFlags != 0 && state.CallContext != null)
            {
                var ctx = state.CallContext; // CtxEdx / CtxImportFlags / CtxSt0Raw of the calling guest call (scratch until the call exits)
                *(uint*)(ctx + 48) = state.ExtEdx;
                *(uint*)(ctx + 60) = state.ExtFlags;
                if ((state.ExtFlags & 2) != 0) state.ExtSt0.AsSpan().CopyTo(new Span<byte>(ctx + 64, 10));
                eax |= 1UL << 63;
            }
            return eax;
        }
        catch (Exception error)
        {
            module.LastImportFault = error;
            var faulted = module.threadState.Value!;
            faulted.PendingFault ??= error;
            if (faulted.CallContext != null) *(uint*)(faulted.CallContext + 32) = 1; // makes the import stub abandon the guest call
            return pop;
        }
    }

    private GuestStack RentStack()
    {
        lock (resourceLock)
        {
            if (idleStacks.TryPop(out var idle)) return idle;
        }
        var ps = (uint)PlatformMemory.Current.PageSize;
        var total = (nuint)(((uint)options.StackSize + ps - 1) / ps * ps + ps);
        var map = PlatformMemory.Current.MapLow(total, out var errno);
        if (map == 0) throw new GuestAddressSpaceException($"Cannot allocate a guest stack of {options.StackSize} bytes in low memory (OS error {errno}).");
        PlatformMemory.Current.Protect(map, ps, MemoryProtection.None); // guard page below the stack
        var stack = new GuestStack { Map = map, MapLength = total, Low = (uint)map + ps, Top = (uint)((ulong)map + total) };
        lock (resourceLock) allStacks.Add(stack);
        Memory.Add(new GuestRegion(stack.Low, stack.Top, null));
        return stack;
    }

    private GuestStack FindStack(uint esp)
    {
        lock (resourceLock)
            foreach (var s in allStacks)
                if (esp >= s.Low && esp <= s.Top) return s;
        throw new GuestStackException($"Nested guest call from ESP 0x{esp:X8}, which is not inside any guest stack.");
    }

    private void ReturnStack(GuestStack stack)
    {
        lock (resourceLock) idleStacks.Push(stack);
    }

    // ---- buffers ----

    internal GuestBuffer AllocateBuffer(int size)
    {
        var ps = (nuint)PlatformMemory.Current.PageSize;
        var length = ((nuint)size + ps - 1) / ps * ps;
        var map = PlatformMemory.Current.MapLow(length, out var errno);
        if (map == 0) throw new GuestAddressSpaceException($"Cannot allocate {size} byte(s) of guest scratch memory in low memory (OS error {errno}).");
        var buffer = new GuestBuffer(this, map, length, size);
        lock (resourceLock) buffers[buffer] = (map, length);
        Memory.Add(new GuestRegion((ulong)map, (ulong)map + (ulong)size, null) { Kind = GuestPageKind.Buffer });
        return buffer;
    }

    internal void ReleaseBuffer(GuestBuffer buffer, nint map, nuint length)
    {
        lock (resourceLock)
        {
            if (!buffers.Remove(buffer)) return; // the module already freed it
            Memory.Remove((ulong)map);
            PlatformMemory.Current.Free(map, length);
        }
    }

    // ---- lifetime ----

    internal void EnterCall()
    {
        lifetime.EnterReadLock();
        if (disposed)
        {
            lifetime.ExitReadLock();
            throw new ObjectDisposedException(nameof(GuestModule), "The guest module has been disposed.");
        }
        threadState.Value!.Holds++;
    }

    internal void ExitCall()
    {
        threadState.Value!.Holds--;
        lifetime.ExitReadLock();
    }

    public void Dispose() => TryDispose(Timeout.Infinite);

    /// <summary>
    /// Disposes the module, waiting at most <paramref name="millisecondsTimeout"/> for in-flight calls to finish. Returns false (and changes nothing) when a call is still running
    /// after that time: a guest thread that never leaves the module (a mixer or worker loop started by the library) would otherwise make disposal wait forever.
    /// Only process exit uses a finite timeout: Windows ends the other threads of an exiting process, a managed host cannot, so the module is abandoned to the operating system.
    /// </summary>
    internal bool TryDispose(int millisecondsTimeout)
    {
        if (disposed) return true;
        if (threadState.IsValueCreated && threadState.Value!.Holds != 0)
            throw new InvalidOperationException("A guest module cannot be disposed from inside one of its active calls or imports.");
        if (DependentCount != 0)
            throw new InvalidOperationException($"The guest module at 0x{ImageBase:X8} still has {DependentCount} live dependent module(s) bound to its exports; dispose them first (or dispose the GuestModuleSet).");
        if (!lifetime.TryEnterWriteLock(millisecondsTimeout)) return false;
        try
        {
            if (disposed) return true;
            initializing = true; // detach notifications must not trigger DLL_THREAD_ATTACH for the disposing thread
            if (tlsProcessAttached)
            {
                try
                {
                    if (threadState.IsValueCreated && threadState.Value!.Teb is not null) RunTlsCallbacks(DllThreadDetach);
                    RunTlsCallbacks(DllProcessDetach);
                }
                catch (Exception error) { DetachError = error; }
            }
            if (attached)
            {
                try
                {
                    var detach = ResolveEntry(entryRva);
                    InvokeCore(detach, [artifact.GuestImageBase, 0u, 0u]);
                }
                catch (Exception error) { DetachError ??= error; }
            }
            Teardown();
            return true;
        }
        finally { lifetime.ExitWriteLock(); }
    }

    private void Teardown()
    {
        if (registrySlot >= 0) env.Retire(registrySlot, Name, artifact.GuestImageBase, registryBound);   // dead before the host image goes away
        registrySlot = -1;
        env.UnregisterTls(this);
        Memory.Clear();
        env.Detach(this);
        lock (resourceLock)
        {
            foreach (var (_, (map, length)) in buffers) PlatformMemory.Current.Free(map, length);
            buffers.Clear();
            foreach (var stack in allStacks) PlatformMemory.Current.Free(stack.Map, stack.MapLength);
            allStacks.Clear();
            idleStacks.Clear();
        }
        PlatformMemory.Current.Free(hostMap, hostMapLength);
        PlatformMemory.Current.Free(guestMap, guestMapLength);
        hostMap = 0;
        guestMap = 0;
        Volatile.Write(ref disposed, true);
        threadState.Dispose();
    }
}
