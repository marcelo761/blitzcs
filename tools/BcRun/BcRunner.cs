using System.Buffers.Binary;
using System.Text;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.CodeGen;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Win32;

namespace BlitzCS.BcRun;

/// <summary>What to run and how (see <see cref="BcRunner.Run"/>).</summary>
public sealed class BcRunOptions
{
    public required string ExePath { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    /// <summary>"windows-x64" or "linux-x64"/"linux-sysv" (aliases of <see cref="BinaryTargets"/>). Null: the host. An artifact only runs on its own host OS.</summary>
    public string? TargetOs { get; init; }
    /// <summary>Guest base; null: chosen by <see cref="ModuleBaseAllocator"/> for the target (the image's own base when it is usable there).</summary>
    public uint? Base { get; init; }
    /// <summary>Load despite unsupported-instruction Errors; the sites become named traps at execution.</summary>
    public bool AllowTranslationErrors { get; init; }
    public Stream? Stdin { get; init; }
    public Stream? Stdout { get; init; }
    public Stream? Stderr { get; init; }
    /// <summary>Environment variables of the guest process (the host environment is never inherited).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>Directory that C:\ maps to. Null: the directory of the EXE.</summary>
    public string? SandboxRoot { get; init; }
    /// <summary>Translation cache directory; null: translate every time.</summary>
    public string? CacheDirectory { get; init; }
    /// <summary>Progress and diagnostics about the run itself (not the guest's output). Null: silent.</summary>
    public Action<string>? Log { get; init; }
    /// <summary>LoadLibrary of a DLL found in the sandbox loads it as a guest module (translated on first use, cached when <see cref="CacheDirectory"/> is set). False: only emulated DLLs load.</summary>
    public bool GuestDlls { get; init; } = true;
    /// <summary>Logs every Win32 call the guest makes (dll!name).</summary>
    public bool TraceCalls { get; init; }
}

/// <summary>Outcome of a run. <see cref="Failure"/> is null when the guest ended by itself (ExitProcess or return from the entry point).</summary>
public sealed record BcRunResult(uint ExitCode, bool CalledExit, string? Failure, uint Base, int TranslationErrors, IReadOnlyList<string> UnboundImports)
{
    /// <summary>Named reasons of failed LoadLibrary/GetProcAddress calls of the guest (guest DLL loader).</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public bool Completed => Failure is null;
}

/// <summary>Runs a PE32 EXE as a guest process: translate, load at an allocated base, build the Win32 environment, run the entry point (docs/binary-win32.md, "Running an EXE").</summary>
public static class BcRunner
{
    /// <summary>Process exit code of BcRun when the run itself failed (not the guest's code).</summary>
    public const int RunnerFailure = 125;

    public static BcRunResult Run(BcRunOptions o)
    {
        var log = o.Log ?? (_ => { });
        uint resolvedBase = 0;
        var path = Path.GetFullPath(o.ExePath);
        var bytes = File.ReadAllBytes(path);
        var image = PeReader.Read(bytes);
        if (image.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return Failed("The file is not a translatable PE32 image: " + string.Join("; ", image.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).Select(d => d.Code + " " + d.Message)));
        if (image.IsDll) return Failed("The image is a DLL; BcRun runs executables.");
        if (image.EntryPointRva == 0) return Failed("The image has no entry point.");

        var target = BinaryTargets.TryNormalize(o.TargetOs) ?? (o.TargetOs is null ? BinaryTargets.Host : null);
        if (target is null) return Failed($"Unknown target '{o.TargetOs}'.");
        if (target != BinaryTargets.Host) return Failed($"Target {target} cannot run on this host ({BinaryTargets.Host}): an artifact is bound to the host OS calling convention. Run BcRun on {target} (for windows-x64 under Wine).");

        var name = Path.GetFileName(path);
        var guestBase = o.Base ?? ModuleBaseAllocator.Allocate([(name, image)], null, target)[0].Base;
        resolvedBase = guestBase;
        var rebased = guestBase != image.ImageBase;
        var loadImage = rebased ? image.Rebase(guestBase) : image;
        log($"image {name}: preferred base 0x{image.ImageBase:X8}, guest base 0x{guestBase:X8}{(rebased ? " (rebased)" : "")}, entry RVA 0x{image.EntryPointRva:X}, {image.Imports.Count} imports");

        var options = new BinaryCompilerOptions(OptimizationProfile.Release, TargetOs: target, LoadBase: guestBase) { DynamicStubs = ImportsLoader(image) ? 256 : 0 };
        var key = CacheKey.Compute(image, options);
        TranslationArtifact? artifact = null;
        TranslationCache? cache = o.CacheDirectory is { } cd ? new TranslationCache(cd) : null;
        if (cache is not null && cache.TryGet(key, out var hit) && hit is not null) { artifact = hit; log("translation cache hit"); }
        if (artifact is null)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            artifact = X64Translator.Translate(image, options, key);
            log($"translated in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
            cache?.Put(key, artifact);
        }
        var errors = artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        foreach (var d in errors.Take(3)) log($"translation error {d.Code}: {d.Message}");
        if (errors.Count > 0) log($"{errors.Count} translation error(s) in total ({string.Join(", ", errors.GroupBy(d => d.Code).Select(g => g.Key + " x" + g.Count()))})");
        if (errors.Count > 0 && !o.AllowTranslationErrors)
            return Failed($"{errors.Count} translation error(s); the unsupported sites would be named traps at run time. Use --allow-errors to run anyway.", errors.Count);

        var (osMajor, osMinor) = OsVersionOf(bytes);
        var version = Win32ProcessInfo.VersionForImage(osMajor, osMinor);
        log($"image declares OS {osMajor}.{osMinor}; guest sees {version.Major}.{version.Minor}.{version.Build}");
        var sandbox = o.SandboxRoot ?? Path.GetDirectoryName(path)!;
        var loader = o.GuestDlls ? new GuestLibraryLoader(new GuestLibraryOptions { TargetOs = target, CacheDirectory = o.CacheDirectory, AllowTranslationErrors = o.AllowTranslationErrors, Log = o.Log }) : null;
        var win32 = new Win32Options
        {
            CommandLine = BuildCommandLine("C:\\" + name, o.Args),
            MainModulePath = "C:\\" + name,
            MainModuleBase = guestBase,
            Version = version,
            SandboxRoot = sandbox,
            Environment = o.Environment,
            Stdin = o.Stdin ?? Console.OpenStandardInput(),
            Stdout = o.Stdout ?? Console.OpenStandardOutput(),
            Stderr = o.Stderr ?? Console.OpenStandardError(),
            CallTrace = o.TraceCalls ? (m => log("call " + m)) : null,
            GuestLibraries = loader,
        };
        List<string> unbound;
        using (var analysis = new Win32ImportResolver(win32))
            unbound = analysis.Analyze(loadImage).Where(v => v.Status == Win32ImportStatus.Missing).Select(v => v.Label).ToList();
        if (unbound.Count > 0) log($"{unbound.Count} import(s) are not provided and fault by name when called: {string.Join(", ", unbound)}");

        var processInfo = Win32ProcessInfo.From(win32);
        var set = loader?.Set ?? new GuestModuleSet(Win32ImportResolver.SharedFactory(win32));
        loader?.Attach(win32, processInfo, loadImage);
        using (loader as IDisposable ?? set)
        {
            GuestModule module;
            try
            {
                set.Add(name, artifact, loadImage, null, new GuestModuleOptions { AllowTranslationErrors = o.AllowTranslationErrors, AllowUnboundImports = true, Process = processInfo });
                set.LoadAll();
                module = set[name];
            }
            catch (GuestException e) { return Failed($"load failed: {e.GetType().Name}: {e.Message}", errors.Count, unbound); }
            try
            {
                var r = module.RunExecutable();
                log(r.CalledExit ? $"guest called ExitProcess({r.ExitCode})" : $"guest entry point returned {r.ExitCode}");
                Flush(win32);
                return new BcRunResult(r.ExitCode, r.CalledExit, null, guestBase, errors.Count, unbound) { Diagnostics = loader?.Diagnostics ?? [] };
            }
            catch (GuestException e)
            {
                Flush(win32);
                log($"guest run failed (the process state is disposed next): {e.GetType().Name}: {e.Message}");
                return Failed($"{e.GetType().Name}: {e.Message}" + (e.InnerException is { } inner ? $" [{inner.GetType().Name}: {inner.Message}]" : ""), errors.Count, unbound);
            }
        }

        BcRunResult Failed(string why, int translationErrors = 0, IReadOnlyList<string>? unboundImports = null) => new(0, false, why, resolvedBase, translationErrors, unboundImports ?? []);
    }

    private static readonly string[] LoaderImports = ["LoadLibraryA", "LoadLibraryW", "LoadLibraryExA", "LoadLibraryExW", "GetProcAddress"];
    private static bool ImportsLoader(PeImage image) => image.Imports.Any(i => i.Name is not null && LoaderImports.Contains(i.Name));

    private static void Flush(Win32Options o) { try { o.Stdout?.Flush(); o.Stderr?.Flush(); } catch (IOException) { } }

    /// <summary>MajorOperatingSystemVersion / MinorOperatingSystemVersion of the optional header (offsets 40 and 42).</summary>
    internal static (uint Major, uint Minor) OsVersionOf(byte[] pe)
    {
        var opt = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C)) + 24;
        return (BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(opt + 40)), BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(opt + 42)));
    }

    /// <summary>The command line the guest sees: the quoted program path, then the arguments quoted by the CommandLineToArgvW rules.</summary>
    public static string BuildCommandLine(string program, IEnumerable<string> args)
    {
        var sb = new StringBuilder().Append('"').Append(program).Append('"');
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    private static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < a.Length; i++)
        {
            var slashes = 0;
            while (i < a.Length && a[i] == '\\') { slashes++; i++; }
            if (i == a.Length) { sb.Append('\\', slashes * 2); break; }
            if (a[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(a[i]);
        }
        return sb.Append('"').ToString();
    }
}
