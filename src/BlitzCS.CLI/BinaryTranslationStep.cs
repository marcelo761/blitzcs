using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

/// <summary>Translates catalog 'binaryLibraries' (PE32 x86) into x64 artifacts next to the output. A no-op for programs without them.</summary>
internal static class BinaryTranslationStep
{
    internal static void Run(ExternalCatalogResult catalog, string outputPath, TextWriter output, TextWriter? warnings = null, string? target = null) =>
        Prepare(catalog, outputPath, warnings, target).Publish(output);

    /// <summary>Environment override of the default target (used by test hosts that must run the artifacts they build).</summary>
    internal const string TargetEnvironmentVariable = "BLITZCS_BINARY_TARGET";

    /// <summary>Artifacts are target specific. Precedence: --target, the catalog's binaryTarget, BLITZCS_BINARY_TARGET, then windows-x64 (the primary target).</summary>
    internal static string ResolveTarget(ExternalCatalogResult catalog, string? requested) => ResolveTarget(catalog.BinaryTarget, requested);

    internal static CompilationTarget ResolveCompilationTarget(ExternalCatalogResult catalog, string? requested) =>
        ResolveCompilationTarget(catalog.BinaryTarget, requested);

    internal static CompilationTarget ResolveCompilationTarget(string? catalogTarget, string? requested)
    {
        if (requested is not null) return TargetCapabilities.ParseTarget(requested);
        if (catalogTarget is not null) return TargetCapabilities.ParseTarget(catalogTarget);
        var fromEnvironment = Environment.GetEnvironmentVariable(TargetEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? CompilationTarget.Win64 : TargetCapabilities.ParseTarget(fromEnvironment);
    }

    internal static string ResolveTarget(string? catalogTarget, string? requested)
    {
        var target = ResolveCompilationTarget(catalogTarget, requested);
        return target.BinaryCompilerTargetOs() ?? target.CanonicalName();
    }

    /// <summary>
    /// The guest base each library must be unpacked at, decided BEFORE any packer stub runs. Linux targets and libraries that are not unpacked: null (preferred base).
    /// windows-x64: the library set is planned with <see cref="ModuleBaseAllocator"/> exactly like the build does for translation, and a packed library whose preferred base is
    /// not usable there (below 0x20000000) or taken by a sibling is unpacked mapped at the planned base. The derived image is then fixed at that base, which
    /// <see cref="Prepare"/> keeps (an image without relocations is placed first and never moved). Throws <see cref="PeRebaseException"/> when the packed file has no relocations and cannot move.
    /// </summary>
    internal static IReadOnlyList<uint?> PlanUnpackBases(IReadOnlyList<BinaryLibrarySpec> specs, string targetOs)
    {
        var bases = new uint?[specs.Count];
        if (!specs.Any(spec => spec.Unpack) || !ModuleBaseAllocator.IsWindowsTarget(targetOs)) return bases;
        foreach (var spec in specs)
        {
            if (!File.Exists(spec.Path)) throw new FileNotFoundException($"Binary library '{spec.FileName}' does not exist.", spec.Path);
            if (!BinaryBuild.IsPe32X86(spec.Path)) throw new ArgumentException($"Binary library '{spec.Path}' is not a PE32 x86 image.");
        }
        var images = specs.Select(spec => PeReader.Read(File.ReadAllBytes(spec.Path))).ToArray();
        var plan = ModuleBaseAllocator.Allocate([.. specs.Select((spec, i) => (spec.FileName, images[i]))], null, targetOs);
        for (var i = 0; i < specs.Count; i++)
            if (specs[i].Unpack && plan[i].Base != images[i].ImageBase) bases[i] = plan[i].Base;
        return bases;
    }

    /// <summary>
    /// Translates (or loads from cache) and validates every library and binding without writing next to the output. With several libraries each gets a load
    /// base from <see cref="ModuleBaseAllocator"/> (a single library keeps its preferred base on Linux; for windows-x64 every library is placed in the Windows image window) and the cache key includes it.
    /// Unprovided imports are reported to <paramref name="warnings"/> (or fail the build for libraries with <c>"unboundImports": "error"</c>).
    /// </summary>
    internal static PreparedBinaries Prepare(ExternalCatalogResult catalog, string outputPath, TextWriter? warnings = null, string? target = null, bool isPackageBuild = false)
    {
        var pending = new List<PendingBinary>();
        if (catalog.BinaryLibraries is not { Count: > 0 } libraries) return new(pending);
        var compilationTarget = ResolveCompilationTarget(catalog, target);
        var specs = catalog.LibrarySpecs ?? [.. libraries.Select(path => new BinaryLibrarySpec(path))];
        foreach (var spec in specs)
            TargetCapabilities.ValidateLegacyBinaryForRecompilation(spec.Path, compilationTarget, isPackageBuild);
        var targetOs = compilationTarget.BinaryCompilerTargetOs()!;
        var profile = catalog.BinaryProfile is null ? OptimizationProfile.Release
            : Enum.TryParse<OptimizationProfile>(catalog.BinaryProfile, ignoreCase: true, out var parsed) ? parsed
            : throw new ArgumentException($"Unknown binaryProfile '{catalog.BinaryProfile}'; expected Debug, Release or ReleasePgo.");
        var cacheDirectory = BinaryCache.Directory();
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var images = specs.Select(spec => PeReader.Read(File.ReadAllBytes(spec.EffectivePath))).ToArray();
        for (var i = 0; i < specs.Count; i++)
            TargetCapabilities.ValidateTargetImportCapabilities(specs[i].FileName, images[i], compilationTarget);
        // windows-x64 rebases every library (a single one too) into the Windows image window; on Linux a single library keeps its preferred base.
        var bases = specs.Count > 1 || ModuleBaseAllocator.IsWindowsTarget(targetOs) ? ModuleBaseAllocator.Allocate([.. specs.Select((spec, i) => (spec.FileName, images[i]))], null, targetOs) : null;
        var artifacts = new TranslationArtifact[specs.Count];
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var library = spec.Path;
            var options = new BinaryCompilerOptions(profile, TargetOs: targetOs, LoadBase: bases is not null && bases[i].Base != images[i].ImageBase ? bases[i].Base : null) { DynamicStubs = spec.EffectiveDynamicStubs(images[i]) };
            var result = BinaryBuild.Translate(spec.EffectivePath, options, cacheDirectory, spec.Preprocessed is { } pre ? [pre.LineageToken] : null);
            // Defence in depth: the artifact that is about to be published must be for the target this build selected, whatever the cache returned.
            if (!string.Equals(result.Artifact.TargetOs ?? BinaryTargets.LinuxSysV, targetOs, StringComparison.Ordinal))
                throw new InvalidOperationException($"Binary library '{Path.GetFileName(library)}': the translation targets '{result.Artifact.TargetOs ?? BinaryTargets.LinuxSysV}' but this build targets '{targetOs}'; clear the binary cache ('{cacheDirectory}').");
            var errors = result.Artifact.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
                throw new InvalidOperationException($"Binary library '{Path.GetFileName(library)}' contains unsupported constructs:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, errors.Take(20).Select(d => $"  {d.Code} at RVA 0x{d.GuestRva ?? 0:X}: {d.Message}"))
                    + (errors.Length > 20 ? $"{Environment.NewLine}  ... {errors.Length - 20} more" : "")
                    + PackedHint(library, errors));
            foreach (var binding in catalog.BinaryBindings?.Where(b => b.LibraryPath == library && b.Intrinsic is null && b.CommandRva is null && b.Win32SystemDll is null) ?? [])
                CheckBinding(binding, result.Artifact);
            artifacts[i] = result.Artifact;
            pending.Add(new(library, Path.Combine(outputDirectory, Path.GetFileName(library) + ".bcx"), new ArtifactSerializer().Serialize(result.Artifact), result, spec));
        }
        CheckImports(specs, images, artifacts, catalog.BinaryBindings ?? [], warnings);
        return new(pending);
    }

    /// <summary>
    /// Imports a library needs: sibling libraries must export the name and be callable (a declared or decorated cdecl/stdcall convention); the rest must
    /// be provided by the Win32 environment. Anything else is unbound: reachable-ness is only knowable at run time, so by default it is a warning
    /// naming dll!name and the call faults naming it; <c>"unboundImports": "error"</c> makes it a build error.
    /// </summary>
    private static void CheckImports(IReadOnlyList<BinaryLibrarySpec> specs, PeImage[] images, TranslationArtifact[] artifacts,
        IReadOnlyList<BinaryBinding> bindings, TextWriter? warnings)
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < specs.Count; i++) byName[GuestModuleSet.NormalizeName(specs[i].FileName)] = i;
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var win32 = spec.Win32 != false ? new BlitzCS.BinaryCompiler.Win32.Win32ImportResolver() : null;
            try
            {
                var verdicts = win32?.Analyze(images[i]);
                var missing = new List<string>();
                foreach (var import in images[i].Imports)
                {
                    var label = $"{import.Dll}!{import.Name ?? "#" + import.Ordinal}";
                    if (!byName.TryGetValue(GuestModuleSet.NormalizeName(import.Dll), out var target))
                    {
                        var implemented = verdicts?.Any(v => v.Dll == import.Dll && v.Name == import.Name && v.Ordinal == import.Ordinal
                            && v.Status == BlitzCS.BinaryCompiler.Win32.Win32ImportStatus.Implemented) ?? false;
                        if (!implemented) missing.Add(label);
                        continue;
                    }
                    var export = images[target].Exports.FirstOrDefault(e => import.Name is not null ? e.Name == import.Name : e.Ordinal == import.Ordinal);
                    if (export is null) { missing.Add(label + " (sibling " + specs[target].FileName + " has no such export)"); continue; }
                    var translated = artifacts[target].Exports.FirstOrDefault(e => e.Ordinal == export.Ordinal);
                    if (translated is null) { missing.Add(label + " (sibling export is not code)"); continue; }
                    var targetSpec = specs[target];
                    var declared = targetSpec.ExportConventions.ContainsKey(export.Name ?? "") || targetSpec.ExportConventions.ContainsKey("#" + export.Ordinal)
                        || bindings.Any(b => b.LibraryPath == targetSpec.Path && b.Export == export.Name && b.Convention is GuestCallingConvention.Cdecl or GuestCallingConvention.Stdcall);
                    if (translated.Convention is GuestCallingConvention.Fastcall or GuestCallingConvention.Thiscall)
                        throw new ArgumentException($"Binary library '{spec.FileName}' imports {label} from {targetSpec.FileName}, a {translated.Convention} export; register conventions cannot be bridged between libraries.");
                    if (!declared && translated.Convention == GuestCallingConvention.Unknown && !targetSpec.DefaultCdecl)
                        throw new ArgumentException($"Binary library '{spec.FileName}' imports {label} from {targetSpec.FileName}, whose convention is undeclared: add defaultConvention \"cdecl\" or an exportConventions entry to the {targetSpec.FileName} library. Conventions are never assumed.");
                }
                if (missing.Count == 0) continue;
                var text = $"Binary library '{spec.FileName}': {missing.Count} import(s) not provided: {string.Join(", ", missing.Take(30))}{(missing.Count > 30 ? ", ..." : "")}";
                if (spec.UnboundImportsAreErrors) throw new InvalidOperationException(text + " (unboundImports is \"error\").");
                warnings?.WriteLine("warning: " + text + "; calling one faults at run time naming dll!name.");
            }
            finally { win32?.Dispose(); }
        }
    }

    /// <summary>Explains BC_ROOT_NOT_FILE_BACKED as a packed image when the structure supports it.</summary>
    private static string PackedHint(string library, BinaryDiagnostic[] errors)
    {
        if (!errors.Any(d => d.Code == "BC_ROOT_NOT_FILE_BACKED")) return "";
        try
        {
            var summary = PackedImageAnalyzer.Analyze(File.ReadAllBytes(library)).Summary;
            return summary.Length == 0 ? "" : $"{Environment.NewLine}  Note: {summary}. See docs/binary-packed-images.md. If this is a packed library, set \"unpack\": true on its binaryLibraries entry (docs/binary-unpack.md).";
        }
        catch (Exception e) when (e is IOException or PeFormatException or UnauthorizedAccessException) { return ""; }
    }

    private static void CheckBinding(BinaryBinding binding, TranslationArtifact artifact)
    {
        var label = $"'{binding.BasicName}' ({Path.GetFileName(binding.LibraryPath)}!{binding.Export})";
        var export = artifact.Exports.FirstOrDefault(e => e.Name == binding.Export)
            ?? throw new ArgumentException($"External function {label}: the translation has no such export.");
        var effective = binding.Convention != GuestCallingConvention.Unknown ? binding.Convention : export.Convention;
        if (effective == GuestCallingConvention.Unknown && binding.LibraryDefaultCdecl) effective = GuestCallingConvention.Cdecl;
        if (effective == GuestCallingConvention.Unknown)
            throw new ArgumentException($"External function {label}: the calling convention cannot be inferred from the export; set binary.convention to 'cdecl', 'stdcall', 'thiscall' or 'fastcall'.");
        if (export.Convention != GuestCallingConvention.Unknown && binding.Convention != GuestCallingConvention.Unknown && export.Convention != binding.Convention)
            throw new ArgumentException($"External function {label}: the catalog says {binding.Convention} but the export decoration says {export.Convention}.");
        var effectiveArgCount = binding.ExportArgCount ?? binding.Parameters.Count;
        if (effective is GuestCallingConvention.Stdcall or GuestCallingConvention.Fastcall && export.ArgBytes is { } bytes && bytes != 4 * effectiveArgCount)
            throw new ArgumentException(effective == GuestCallingConvention.Stdcall
                ? $"External function {label}: stdcall export pops {bytes} byte(s) but the declaration passes {4 * effectiveArgCount}."
                : $"External function {label}: the fastcall decoration says {bytes} byte(s) of parameters but the declaration passes {4 * effectiveArgCount}.");
        if (effective == GuestCallingConvention.Thiscall && (binding.Parameters.Count == 0 || binding.Parameters[0] != ExternalValueType.Integer))
            throw new ArgumentException($"External function {label}: a thiscall binding needs an Integer first parameter, which is passed as 'this' in ECX.");
    }
}

internal sealed record PendingBinary(string Library, string Target, byte[] Bytes, BinaryBuildResult Result, BinaryLibrarySpec Spec);

internal sealed class PreparedBinaries(List<PendingBinary> pending)
{
    internal void Publish(TextWriter output)
    {
        foreach (var (library, target, bytes, result, spec) in pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
            output.WriteLine($"binary: {Path.GetFileName(library)} -> {Path.GetFileName(target)} ({(result.CacheHit ? "cache hit" : "translated")}, {result.Artifact.HostImage.Length} bytes, {result.Artifact.Diagnostics.Count} diagnostic(s), base 0x{result.Artifact.GuestImageBase:X8})");
            if (spec.Preprocessed is { } derived)
            {
                var targetDirectory = Path.GetDirectoryName(target)!;
                var derivedTarget = Path.Combine(targetDirectory, Path.GetFileName(library) + TranslatedLibrary.DerivedExtension);
                var lineageTarget = Path.Combine(targetDirectory, Path.GetFileName(library) + TranslatedLibrary.LineageExtension);
                var lineageSource = Path.ChangeExtension(derived.DerivedPath, ".lineage.json");
                foreach (var protectedPath in new[] { derivedTarget, lineageTarget })
                    if (string.Equals(Path.GetFullPath(protectedPath), Path.GetFullPath(library), StringComparison.Ordinal))
                        throw new ArgumentException("Derived output would overwrite an input library.");
                File.Copy(derived.DerivedPath, derivedTarget, overwrite: true);
                File.Copy(lineageSource, lineageTarget, overwrite: true);
                output.WriteLine($"binary: {Path.GetFileName(library)} preprocessed ({derived.LineageToken}) -> {Path.GetFileName(derivedTarget)}, {Path.GetFileName(lineageTarget)}");
            }
        }
    }
}
