using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BlitzCS.BinaryCompiler;
using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

internal static class ExternalCatalog
{
    private static readonly HashSet<string> ReservedKeywordNames = new(
        ("dim goto gosub return exit if then else endif elseif while wend for to step next function endfunction " +
         "type endtype each local global field const select case default endselect repeat until forever data read restore " +
         "abs sgn mod pi true false int float str include new delete first last insert before after null object handle " +
         "and or xor not shl shr sar print end cs").Split(' '), StringComparer.OrdinalIgnoreCase);

    internal static ExternalCatalogResult Load(string catalogPath, string? target = null, bool isPackageBuild = false)
    {
        var selection = RuntimeCatalogSelector.Resolve(catalogPath, target);
        catalogPath = selection.CatalogPath;
        using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var root = document.RootElement;
        var functions = new List<ExternalFunction>();
        var signatures = new List<ExternalFunctionSignature>();
        var items = root.GetProperty("functions").EnumerateArray().ToArray();
        var nativeLibraries = root.TryGetProperty("nativeLibraries", out var libraries)
            && libraries.ValueKind != JsonValueKind.Null
            ? libraries.EnumerateArray().Select(item => Path.GetFullPath(
                item.GetString() ?? throw new ArgumentException("External catalog 'nativeLibraries' entries cannot be null."),
                Path.GetDirectoryName(catalogPath)!)).ToArray()
            : [];
        var binaryProfile = root.TryGetProperty("binaryProfile", out var profile) && profile.ValueKind == JsonValueKind.String
            ? profile.GetString() : null;
        var binaryTarget = root.TryGetProperty("binaryTarget", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
            ? (TargetCapabilities.TryParseTarget(targetElement.GetString(), out var parsedCatalogTarget)
                ? parsedCatalogTarget.BinaryCompilerTargetOs() ?? parsedCatalogTarget.CanonicalName()
                : throw new ArgumentException($"Unknown binaryTarget '{targetElement.GetString()}'; expected linux-x64 or windows-x64 (or Win32, Linux32, Win64, Linux64)."))
            : null;
        var blitz3dRuntime = root.TryGetProperty("blitz3dRuntime", out var b3dElement) && b3dElement.ValueKind == JsonValueKind.String
            ? b3dElement.GetString() : null;
        if (blitz3dRuntime is not null)
        {
            blitz3dRuntime = Path.GetFullPath(blitz3dRuntime, Path.GetDirectoryName(catalogPath)!);
            if (!File.Exists(blitz3dRuntime))
                throw new FileNotFoundException($"Blitz3D runtime library '{blitz3dRuntime}' does not exist.", blitz3dRuntime);
        }
        string Read(JsonElement item, string key) => item.GetProperty(key).GetString()
            ?? throw new ArgumentException($"External catalog '{key}' cannot be null.");
        var catalogDirectory = Path.GetDirectoryName(catalogPath)!;
        var explicitNames = new HashSet<string>(
            items.Select(item => Read(item, "name")),
            StringComparer.OrdinalIgnoreCase);
        var userLibDecls = ReadUserLibDeclarations(root, catalogDirectory, explicitNames);
        var directUserLibDecls = new List<UserLibFunctionDeclaration>();
        var bcsbreUserLibDecls = new List<UserLibFunctionDeclaration>();
        var compilationTarget = BinaryTranslationStep.ResolveCompilationTarget(binaryTarget, target);
        var nativeRuntimeCommands = blitz3dRuntime is not null && compilationTarget == CompilationTarget.Win32;
        string? nativeRuntimeSha256 = null;
        if (blitz3dRuntime is not null)
        {
            if (nativeRuntimeCommands)
            {
                nativeRuntimeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(blitz3dRuntime))).ToLowerInvariant();
                if (!string.Equals(nativeRuntimeSha256, NativeBlitzCommandLibrary.SupportedRuntimeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Blitz3D runtime '{Path.GetFileName(blitz3dRuntime)}' has unsupported SHA-256 '{nativeRuntimeSha256}' for native Win32 command calls; supported runtime SHA-256 is '{NativeBlitzCommandLibrary.SupportedRuntimeSha256}'.");
                if (!nativeLibraries.Any(path => PathEquals(path, blitz3dRuntime)))
                    nativeLibraries = [.. nativeLibraries, blitz3dRuntime];
            }
            else
            {
                TargetCapabilities.ValidateLegacyBinaryForRecompilation(blitz3dRuntime, compilationTarget, isPackageBuild);
            }
        }
        foreach (var group in userLibDecls.GroupBy(d => d.LibraryPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (group.First().IsWin32SystemDll)
            {
                if (compilationTarget == CompilationTarget.Win32)
                {
                    directUserLibDecls.AddRange(group);
                }
                else
                {
                    bcsbreUserLibDecls.AddRange(group);
                }
                continue;
            }

            var resolution = TargetCapabilities.ResolveDependency(group.Key, compilationTarget);
            if (resolution == DependencyResolutionKind.NativeDirect)
            {
                if (!nativeLibraries.Any(p => PathEquals(p, group.Key)))
                    nativeLibraries = [.. nativeLibraries, group.Key];
                directUserLibDecls.AddRange(group);
            }
            else
            {
                bcsbreUserLibDecls.AddRange(group);
            }
        }
        var binaryItems = items.Where(item => item.TryGetProperty("binary", out var binary) && binary.ValueKind != JsonValueKind.Null).ToArray();
        var specs = ReadLibrarySpecs(root, binaryItems, bcsbreUserLibDecls, directUserLibDecls, catalogDirectory, compilationTarget, isPackageBuild,
            nativeRuntimeCommands ? null : blitz3dRuntime, nativeRuntimeCommands ? blitz3dRuntime : null);
        var binaryLibraries = specs.Select(spec => spec.Path).ToArray();
        var explicitBinaryBindings = binaryItems.Select(item => ReadBinding(item, catalogDirectory, specs, compilationTarget, isPackageBuild)).ToArray();
        var userLibBinaryBindings = bcsbreUserLibDecls.Select(decl => ResolveUserLibBinaryBinding(decl, specs, compilationTarget, isPackageBuild)).ToArray();
        var runtimeBindings = new List<BinaryBinding>();
        var runtimeCmdMap = new Dictionary<string, Blitz3dRuntimeCommand>(StringComparer.OrdinalIgnoreCase);
        if (blitz3dRuntime is not null)
        {
            var commands = Blitz3dRuntimeExtractor.ExtractCommands(blitz3dRuntime);
            var existingNames = new HashSet<string>(explicitNames, StringComparer.OrdinalIgnoreCase);
            foreach (var u in userLibDecls) existingNames.Add(u.BasicName);
            foreach (var cmd in commands)
            {
                if (ReservedKeywordNames.Contains(cmd.BasicName))
                    continue;
                if (!existingNames.Add(cmd.BasicName))
                    continue;
                runtimeCmdMap[cmd.BasicName] = cmd;
                runtimeBindings.Add(new BinaryBinding(
                    BasicName: cmd.BasicName,
                    LibraryPath: blitz3dRuntime,
                    Export: "",
                    Convention: GuestCallingConvention.Cdecl,
                    Parameters: cmd.ParameterTypes,
                    ReturnType: cmd.ReturnType,
                    LibraryDefaultCdecl: false)
                {
                    CommandRva = cmd.Rva,
                    FromUserLib = false,
                    NativeCommandSha256 = nativeRuntimeCommands ? nativeRuntimeSha256 : null
                });
            }
        }
        var binaryBindings = explicitBinaryBindings.Concat(userLibBinaryBindings).Concat(runtimeBindings).ToArray();
        DirectoryStaging? staging = null;
        string? bindingAssembly = null;
        string? directUserLibAssembly = null;
        if (binaryBindings.Length > 0 || directUserLibDecls.Count > 0)
        {
            staging = new DirectoryStaging();
            try
            {
                if (binaryBindings.Length > 0)
                    bindingAssembly = BinaryBindingGenerator.Generate(binaryBindings, specs, staging.Path);
                if (directUserLibDecls.Count > 0)
                    directUserLibAssembly = UserLibResolver.GenerateDirectNativeBindings(
                        directUserLibDecls,
                        staging.Path,
                        nativeRuntimeCommands ? Path.GetFileName(blitz3dRuntime!) : null,
                        nativeRuntimeCommands ? nativeRuntimeSha256 : null);
            }
            catch { staging.Dispose(); throw; }
        }
        var boundItems = items.Where(item => item.TryGetProperty("method", out var method) && method.ValueKind != JsonValueKind.Null
            || binaryItems.Contains(item)).ToArray();
        var assemblyPaths = boundItems.Select(item => binaryItems.Contains(item) ? bindingAssembly!
            : Path.GetFullPath(Read(item, "assembly"), catalogDirectory))
            .Concat(userLibBinaryBindings.Length > 0 || runtimeBindings.Count > 0 ? [bindingAssembly!] : [])
            .Concat(directUserLibDecls.Count > 0 ? [directUserLibAssembly!] : [])
            .Distinct()
            .ToArray();
        // Each compilation can bind a different assembly with the same simple name.
        // Unload is requested after metadata inspection; MethodInfo keeps it alive
        // only while the returned registry is observable.
        var context = new ExternalAssemblyLoadContext();
        try
        {
        context.Discover(assemblyPaths);
        var byItem = boundItems.Zip(boundItems.Select(item => binaryItems.Contains(item) ? bindingAssembly!
            : Path.GetFullPath(Read(item, "assembly"), catalogDirectory))).ToDictionary(pair => pair.First, pair => pair.Second);
        var libraryIndexes = BinaryBindingGenerator.Libraries(binaryBindings);
        foreach (var item in items)
        {
            var parameters = item.GetProperty("parameters").EnumerateArray().Select(p => p.GetString() switch
            {
                "Integer" => ExternalValueType.Integer, "Float" => ExternalValueType.Float, "String" => ExternalValueType.String,
                "PointerIn" => ExternalValueType.PointerIn, "PointerOut" => ExternalValueType.PointerOut, "PointerInOut" => ExternalValueType.PointerInOut,
                _ => throw new ArgumentException("External parameter types must be Integer, Float, String, PointerIn, PointerOut or PointerInOut.")
            }).ToArray();
            ExternalFunction? function = null;
            if (byItem.TryGetValue(item, out var assemblyPath))
            {
                var assembly = context.LoadAssembly(assemblyPath);
                var binaryIndex = Array.IndexOf(binaryItems, item);
                var typeName = binaryIndex >= 0 ? BinaryBindingGenerator.TypeName(IndexOf(libraryIndexes, explicitBinaryBindings[binaryIndex].LibraryPath)) : Read(item, "type");
                var type = assembly.GetType(typeName, throwOnError: true)!;
                var methodName = binaryIndex >= 0 ? BinaryBindingGenerator.MethodName(explicitBinaryBindings[binaryIndex].BasicName) : Read(item, "method");
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, parameters.Select(ToSystemType).ToArray())
                    ?? throw new ArgumentException($"External method '{type.FullName}.{methodName}' with the declared signature does not exist.");
                function = new ExternalFunction(Read(item, "name"), method);
            }
            var returnType = item.TryGetProperty("returnType", out var returnTypeElement)
                ? returnTypeElement.GetString() switch
                {
                    "Integer" => ExternalValueType.Integer, "Float" => ExternalValueType.Float, "String" => ExternalValueType.String,
                    "Void" => ExternalValueType.Void,
                    _ => throw new ArgumentException("External return types must be Integer, Float, String or Void.")
                }
                : function?.Signature.ReturnType ?? throw new ArgumentException("External declarations without a method require a returnType.");
            object[]? defaults = null;
            if (item.TryGetProperty("defaults", out var defaultsElement))
            {
                if (defaultsElement.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("External catalog 'defaults' must be an array of trailing typed constants.");
                var values = defaultsElement.EnumerateArray().ToArray();
                if (values.Length > parameters.Length)
                    throw new ArgumentException("External catalog 'defaults' cannot exceed the parameter count.");
                defaults = values.Select((value, index) => ReadDefault(value,
                    parameters[parameters.Length - values.Length + index])).ToArray();
            }
            var signature = new ExternalFunctionSignature(Read(item, "name"), returnType, parameters, defaults);
            if (function is not null && (function.Signature.ReturnType != signature.ReturnType || !function.Signature.ParameterTypes.SequenceEqual(signature.ParameterTypes)))
                throw new ArgumentException($"External method '{function.Name}' does not match its declared signature.");
            signatures.Add(signature);
            if (function is not null) functions.Add(function);
        }
        if (userLibBinaryBindings.Length > 0 || runtimeBindings.Count > 0)
        {
            var assembly = context.LoadAssembly(bindingAssembly!);
            foreach (var binding in userLibBinaryBindings)
            {
                var typeName = BinaryBindingGenerator.TypeName(IndexOf(libraryIndexes, binding.LibraryPath));
                var type = assembly.GetType(typeName, throwOnError: true)!;
                var methodName = BinaryBindingGenerator.MethodName(binding.BasicName);
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, binding.Parameters.Select(ToSystemType).ToArray())
                    ?? throw new ArgumentException($"External method '{type.FullName}.{methodName}' with the declared signature does not exist.");
                signatures.Add(new ExternalFunctionSignature(binding.BasicName, binding.ReturnType, binding.Parameters, null, binding.PointerParameters));
                functions.Add(new ExternalFunction(binding.BasicName, method));
            }
            foreach (var binding in runtimeBindings)
            {
                var cmd = runtimeCmdMap[binding.BasicName];
                var typeName = BinaryBindingGenerator.TypeName(IndexOf(libraryIndexes, binding.LibraryPath));
                var type = assembly.GetType(typeName, throwOnError: true)!;
                var methodName = BinaryBindingGenerator.MethodName(binding.BasicName);
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, binding.Parameters.Select(ToSystemType).ToArray())
                    ?? throw new ArgumentException($"External method '{type.FullName}.{methodName}' with the declared signature does not exist.");
                signatures.Add(new ExternalFunctionSignature(binding.BasicName, binding.ReturnType, binding.Parameters, cmd.DefaultValues));
                functions.Add(new ExternalFunction(binding.BasicName, method));
            }
        }
        if (directUserLibDecls.Count > 0)
        {
            var assembly = context.LoadAssembly(directUserLibAssembly!);
            var type = assembly.GetType("BlitzCS.NativeUserLibBindings.DirectUserLibs", throwOnError: true)!;
            foreach (var decl in directUserLibDecls)
            {
                var methodName = BinaryBindingGenerator.MethodName(decl.BasicName);
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, decl.ParameterTypes.Select(ToSystemType).ToArray())
                    ?? throw new ArgumentException($"External method '{type.FullName}.{methodName}' with the declared signature does not exist.");
                signatures.Add(new ExternalFunctionSignature(decl.BasicName, decl.ReturnType, decl.ParameterTypes, null, decl.PointerParameters));
                functions.Add(new ExternalFunction(decl.BasicName, method));
            }
        }
        return new(ExternalFunctionRegistry.FromDeclarations(signatures, functions), nativeLibraries.Concat(binaryLibraries).ToArray(), binaryLibraries, binaryProfile, binaryBindings, staging, specs, binaryTarget, selection.CatalogInputs);
        }
        catch { staging?.Dispose(); throw; }
        finally { context.Dispose(); }
    }


    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static IReadOnlyList<UserLibFunctionDeclaration> ReadUserLibDeclarations(
        JsonElement root,
        string catalogDirectory,
        HashSet<string> explicitNames)
    {
        if (!root.TryGetProperty("userlibs", out var userlibsElement) || userlibsElement.ValueKind == JsonValueKind.Null)
            return [];
        if (userlibsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("External catalog 'userlibs' must be an array of .decls file paths.");

        var declarations = new List<UserLibFunctionDeclaration>();
        var seen = new HashSet<string>(explicitNames, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in userlibsElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                throw new ArgumentException("External catalog 'userlibs' entries must be non-empty .decls file path strings.");
            var declsPath = Path.GetFullPath(entry.GetString()!, catalogDirectory);
            foreach (var decl in UserLibResolver.ParseDeclsFile(declsPath, catalogDirectory))
            {
                if (explicitNames.Contains(decl.BasicName))
                    continue;
                if (!seen.Add(decl.BasicName))
                    throw new ArgumentException($"Userlib function '{decl.BasicName}' in '{Path.GetFileName(declsPath)}' is declared more than once.");
                declarations.Add(decl);
            }
        }
        return declarations;
    }

    private static BinaryBinding ResolveUserLibBinaryBinding(
        UserLibFunctionDeclaration decl,
        IReadOnlyList<BinaryLibrarySpec> specs,
        CompilationTarget compilationTarget,
        bool isPackageBuild)
    {
        if (decl.IsWin32SystemDll)
        {
            if (specs.Count == 0)
                throw new InvalidOperationException($"External userlib function '{decl.BasicName}' ({decl.LibraryPath}!{decl.ExportName}) is a Win32 system DLL call, which requires at least one binary library or 'blitz3dRuntime' to provide the emulated guest memory context.");
            var hostLib = specs[0].Path;
            var sysDllName = decl.Win32SystemDllName ?? Path.GetFileName(decl.LibraryPath);
            return new BinaryBinding(
                BasicName: decl.BasicName,
                LibraryPath: hostLib,
                Export: decl.ExportName,
                Convention: GuestCallingConvention.Stdcall,
                Parameters: decl.ParameterTypes,
                ReturnType: decl.ReturnType,
                LibraryDefaultCdecl: false)
            {
                PointerParameters = decl.PointerParameters,
                Win32SystemDll = sysDllName,
                FromUserLib = true
            };
        }

        var libraryName = Path.GetFileName(decl.LibraryPath);
        TargetCapabilities.ValidateLegacyBinaryForRecompilation(decl.LibraryPath, compilationTarget, isPackageBuild);
        var spec = specs.First(candidate => PathEquals(candidate.Path, decl.LibraryPath));
        var image = PeReader.Read(File.ReadAllBytes(spec.EffectivePath));
        var requested = decl.ExportName;
        var exact = image.Exports.Where(e => e.Name == requested).ToArray();
        var candidates = exact.Length > 0 ? exact : image.Exports.Where(e => e.Name is not null
            && (e.Name == "_" + requested || e.Name.StartsWith("_" + requested + "@", StringComparison.Ordinal) || e.Name.StartsWith(requested + "@", StringComparison.Ordinal)
                || e.Name.StartsWith("@" + requested + "@", StringComparison.Ordinal))).ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException($"Binary library '{libraryName}' has no export '{requested}' (declared by '{decl.BasicName}' in '{Path.GetFileName(decl.SourceDeclsPath)}').");
        if (candidates.Length > 1)
            throw new ArgumentException($"Binary library '{libraryName}' export '{requested}' is ambiguous ({string.Join(", ", candidates.Select(c => c.Name + "#" + c.Ordinal))}); name the exact export.");
        if (candidates[0].ForwarderTarget is { } forwarder)
            throw new ArgumentException($"Binary library '{libraryName}' export '{requested}' is a forwarder to '{forwarder}', which is unsupported.");
        var exportName = candidates[0].Name!;
        var convention = GuestCallingConvention.Unknown;
        int? exportArgCount = null;
        var (hintConvention, hintBytes) = ExportAbiHints.Infer(exportName);
        if (hintBytes is { } bytes && bytes > 4 * decl.ParameterTypes.Count)
        {
            exportArgCount = bytes / 4;
        }
        if (spec.ExportConventions.TryGetValue(exportName, out var mapped) || spec.ExportConventions.TryGetValue("#" + candidates[0].Ordinal, out mapped))
        {
            if (mapped.Convention == GuestCallingConvention.Stdcall && mapped.ArgCount < decl.ParameterTypes.Count)
                throw new ArgumentException($"External function '{decl.BasicName}' ({libraryName}!{exportName}): exportConventions says stdcall:{mapped.ArgCount} but the declaration has {decl.ParameterTypes.Count} parameter(s).");
            if (mapped.Convention == GuestCallingConvention.Stdcall && mapped.ArgCount > decl.ParameterTypes.Count)
                exportArgCount = mapped.ArgCount;
            convention = mapped.Convention;
        }
        else if (hintConvention != GuestCallingConvention.Unknown)
        {
            convention = hintConvention;
        }
        else
        {
            convention = spec.DefaultCdecl ? GuestCallingConvention.Cdecl : GuestCallingConvention.Stdcall;
        }
        return new BinaryBinding(decl.BasicName, decl.LibraryPath, exportName, convention, decl.ParameterTypes, decl.ReturnType, spec.DefaultCdecl)
        {
            PointerParameters = decl.PointerParameters,
            ExportArgCount = exportArgCount,
            FromUserLib = true
        };
    }

    /// <summary>
    /// Merges the <c>binaryLibraries</c> entries with the libraries named by function bindings (implicit entries with defaults), rejects duplicate files
    /// and duplicate output file names, and runs the opt-in preprocessors (<c>unpack</c>).
    /// </summary>
    private static List<BinaryLibrarySpec> ReadLibrarySpecs(JsonElement root, JsonElement[] binaryItems,
        IReadOnlyList<UserLibFunctionDeclaration> bcsbreUserLibDecls, IReadOnlyList<UserLibFunctionDeclaration> directUserLibDecls,
        string catalogDirectory, CompilationTarget compilationTarget, bool isPackageBuild, string? blitz3dRuntime = null,
        string? nativeBlitz3dRuntime = null)
    {
        var specs = new List<BinaryLibrarySpec>();
        if (blitz3dRuntime is not null)
        {
            specs.Add(new BinaryLibrarySpec(blitz3dRuntime));
        }
        if (root.TryGetProperty("binaryLibraries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
            {
                var spec = BinaryLibrarySpec.Parse(entry, catalogDirectory);
                var isWin32NativeDependency = compilationTarget == CompilationTarget.Win32
                    && directUserLibDecls.Any(decl => PathEquals(decl.LibraryPath, spec.Path));
                if (nativeBlitz3dRuntime is not null && PathEquals(spec.Path, nativeBlitz3dRuntime)
                    || isWin32NativeDependency && !binaryItems.Any(item =>
                    {
                        var binary = item.GetProperty("binary");
                        return binary.ValueKind == JsonValueKind.Object
                            && binary.TryGetProperty("library", out var library)
                            && library.ValueKind == JsonValueKind.String
                            && PathEquals(Path.GetFullPath(library.GetString()!, catalogDirectory), spec.Path);
                    }))
                    continue;
                var existing = specs.FindIndex(candidate => PathEquals(candidate.Path, spec.Path));
                if (existing >= 0)
                {
                    var fromBlitzRuntime = blitz3dRuntime is not null && PathEquals(specs[existing].Path, blitz3dRuntime);
                    if (!fromBlitzRuntime && (specs[existing].Explicit || spec.Explicit))
                        throw new ArgumentException($"External catalog lists binary library '{spec.FileName}' more than once; its options would be ambiguous.");
                    specs[existing] = spec;
                    continue;
                }
                specs.Add(spec);
            }
        else if (root.TryGetProperty("binaryLibraries", out var other) && other.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("External catalog 'binaryLibraries' must be an array.");
        foreach (var item in binaryItems)
        {
            var binary = item.GetProperty("binary");
            if (binary.ValueKind != JsonValueKind.Object || !binary.TryGetProperty("library", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var path = Path.GetFullPath(name.GetString()!, catalogDirectory);
            if (!specs.Any(candidate => PathEquals(candidate.Path, path))) specs.Add(new BinaryLibrarySpec(path));
        }
        foreach (var decl in bcsbreUserLibDecls)
        {
            if (!decl.IsWin32SystemDll && !specs.Any(candidate => PathEquals(candidate.Path, decl.LibraryPath)))
                specs.Add(new BinaryLibrarySpec(decl.LibraryPath));
        }
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
            if (names.TryGetValue(spec.FileName, out var other) && !PathEquals(other, spec.Path))
                throw new ArgumentException($"Binary libraries '{other}' and '{spec.Path}' have the same file name; modules are published side by side and linked by name.");
            else names[spec.FileName] = spec.Path;
        foreach (var spec in specs)
            TargetCapabilities.ValidateLegacyBinaryForRecompilation(spec.Path, compilationTarget, isPackageBuild);
        if (specs.Count == 0) return specs;
        var targetOs = compilationTarget.BinaryCompilerTargetOs()!;
        var work = BinaryCache.Directory();
        var unpackBases = BinaryTranslationStep.PlanUnpackBases(specs, targetOs);
        for (var i = 0; i < specs.Count; i++) specs[i] = specs[i].Preprocess(work, unpackBases[i]);
        return specs;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        throw new InvalidOperationException("Binary library index not found.");
    }

    private static ExternalValueType ParseType(string? name, bool allowVoid) => name switch
    {
        "Integer" => ExternalValueType.Integer, "Float" => ExternalValueType.Float, "String" => ExternalValueType.String,
        "Void" when allowVoid => ExternalValueType.Void,
        _ => throw new ArgumentException("External parameter types must be Integer, Float or String.")
    };

    /// <summary>Validates one catalog 'binary' entry against the PE export table (no translation needed).</summary>
    private static BinaryBinding ReadBinding(JsonElement item, string catalogDirectory, IReadOnlyList<BinaryLibrarySpec> specs, CompilationTarget compilationTarget, bool isPackageBuild)
    {
        var name = item.GetProperty("name").GetString() ?? throw new ArgumentException("External catalog 'name' cannot be null.");
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"External BASIC name '{name}' requires a letter followed by letters, digits or underscores.");
        if (item.TryGetProperty("method", out var method) && method.ValueKind != JsonValueKind.Null
            || item.TryGetProperty("assembly", out var assembly) && assembly.ValueKind != JsonValueKind.Null)
            throw new ArgumentException($"External function '{name}' cannot combine 'binary' with a managed 'assembly'/'method' binding.");
        var binary = item.GetProperty("binary");
        if (binary.ValueKind != JsonValueKind.Object) throw new ArgumentException($"External function '{name}': 'binary' must be an object.");
        string Field(string key) => binary.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"External function '{name}': binary.{key} is required.");
        var libraryName = Field("library");
        var intrinsic = binary.TryGetProperty("intrinsic", out var intrinsicElement) && intrinsicElement.ValueKind == JsonValueKind.String ? intrinsicElement.GetString() : null;
        if (intrinsic is not null)
        {
            if (binary.TryGetProperty("export", out _)) throw new ArgumentException($"External function '{name}': binary.intrinsic cannot be combined with binary.export.");
            var shapes = new Dictionary<string, (int Parameters, bool Returns)> { ["alloc"] = (1, true), ["free"] = (1, false), ["peek8"] = (1, true), ["peek32"] = (1, true), ["poke8"] = (2, false), ["poke32"] = (2, false) };
            if (!shapes.TryGetValue(intrinsic, out var shape))
                throw new ArgumentException($"External function '{name}': unknown binary.intrinsic '{intrinsic}'; expected {string.Join(", ", shapes.Keys)}.");
            var declared = item.GetProperty("parameters").EnumerateArray().Select(p => ParseType(p.GetString(), false)).ToArray();
            var returned = item.TryGetProperty("returnType", out var r) ? ParseType(r.GetString(), true) : ExternalValueType.Void;
            if (declared.Length != shape.Parameters || declared.Any(t => t != ExternalValueType.Integer) || returned != (shape.Returns ? ExternalValueType.Integer : ExternalValueType.Void))
                throw new ArgumentException($"External function '{name}': intrinsic '{intrinsic}' takes {shape.Parameters} Integer parameter(s) and returns {(shape.Returns ? "Integer" : "Void")}.");
            var libraryPath = Path.GetFullPath(libraryName, catalogDirectory);
            if (!File.Exists(libraryPath)) throw new FileNotFoundException($"Binary library '{libraryName}' for '{name}' does not exist.", libraryPath);
            TargetCapabilities.ValidateLegacyBinaryForRecompilation(libraryPath, compilationTarget, isPackageBuild);
            return new BinaryBinding(name, libraryPath, "", GuestCallingConvention.Unknown, declared, returned) { Intrinsic = intrinsic };
        }
        var requested = Field("export");
        var parameters = item.GetProperty("parameters").EnumerateArray().Select(p => ParseType(p.GetString(), false)).ToArray();
        if (!item.TryGetProperty("returnType", out var returnElement))
            throw new ArgumentException($"External function '{name}': binary functions require a returnType.");
        var returnType = ParseType(returnElement.GetString(), true);
        if (returnType == ExternalValueType.String)
            throw new ArgumentException($"External function '{name}' ({libraryName}!{requested}): string return from binary library unsupported; the pointer ownership is not known.");
        var convention = GuestCallingConvention.Unknown;
        if (binary.TryGetProperty("convention", out var conventionElement) && conventionElement.ValueKind != JsonValueKind.Null)
            convention = conventionElement.GetString()?.ToLowerInvariant() switch
            {
                "cdecl" => GuestCallingConvention.Cdecl, "stdcall" => GuestCallingConvention.Stdcall,
                "thiscall" => GuestCallingConvention.Thiscall, "fastcall" => GuestCallingConvention.Fastcall,
                _ => throw new ArgumentException($"External function '{name}': binary.convention must be 'cdecl', 'stdcall', 'thiscall' or 'fastcall'.")
            };
        if (convention == GuestCallingConvention.Thiscall && (parameters.Length == 0 || parameters[0] != ExternalValueType.Integer))
            throw new ArgumentException($"External function '{name}': a thiscall binding needs an Integer first parameter, which is passed as 'this' in ECX.");
        var library = Path.GetFullPath(libraryName, catalogDirectory);
        if (!File.Exists(library)) throw new FileNotFoundException($"Binary library '{libraryName}' for '{name}' does not exist.", library);
        TargetCapabilities.ValidateLegacyBinaryForRecompilation(library, compilationTarget, isPackageBuild);
        var spec = specs.First(candidate => PathEquals(candidate.Path, library));
        var image = PeReader.Read(File.ReadAllBytes(spec.EffectivePath));
        var exact = image.Exports.Where(e => e.Name == requested).ToArray();
        var candidates = exact.Length > 0 ? exact : image.Exports.Where(e => e.Name is not null
            && (e.Name == "_" + requested || e.Name.StartsWith("_" + requested + "@", StringComparison.Ordinal) || e.Name.StartsWith(requested + "@", StringComparison.Ordinal)
                || e.Name.StartsWith("@" + requested + "@", StringComparison.Ordinal))).ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException($"Binary library '{libraryName}' has no export '{requested}' (declared by '{name}').");
        if (candidates.Length > 1)
            throw new ArgumentException($"Binary library '{libraryName}' export '{requested}' is ambiguous ({string.Join(", ", candidates.Select(c => c.Name + "#" + c.Ordinal))}); name the exact export.");
        if (candidates[0].ForwarderTarget is { } forwarder)
            throw new ArgumentException($"Binary library '{libraryName}' export '{requested}' is a forwarder to '{forwarder}', which is unsupported.");
        var exportName = candidates[0].Name!;
        if ((spec.ExportConventions.TryGetValue(exportName, out var mapped) || spec.ExportConventions.TryGetValue("#" + candidates[0].Ordinal, out mapped)))
        {
            if (convention != GuestCallingConvention.Unknown && convention != mapped.Convention)
                throw new ArgumentException($"External function '{name}' ({libraryName}!{exportName}): binary.convention says {convention} but the library's exportConventions says {mapped.Convention}; declare it once.");
            if (mapped.Convention == GuestCallingConvention.Stdcall && mapped.ArgCount != parameters.Length)
                throw new ArgumentException($"External function '{name}' ({libraryName}!{exportName}): exportConventions says stdcall:{mapped.ArgCount} but the declaration has {parameters.Length} parameter(s).");
            convention = mapped.Convention;
        }
        return new BinaryBinding(name, library, exportName, convention, parameters, returnType, spec.DefaultCdecl);
    }

    private static object ReadDefault(JsonElement value, ExternalValueType type) => type switch
    {
        ExternalValueType.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer) => integer,
        ExternalValueType.Float when value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var single) && float.IsFinite(single) => single,
        ExternalValueType.String when value.ValueKind == JsonValueKind.String => value.GetString()!,
        _ => throw new ArgumentException("External catalog 'defaults' must match trailing parameter types: Int32, finite Single or String.")
    };

    private static Type ToSystemType(ExternalValueType type) => type switch
    {
        ExternalValueType.Integer => typeof(int), ExternalValueType.Float => typeof(float),
        ExternalValueType.String => typeof(string), ExternalValueType.PointerIn => typeof(ReadOnlySpan<byte>),
        ExternalValueType.PointerOut or ExternalValueType.PointerInOut => typeof(Span<byte>), _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

internal sealed record ExternalCatalogResult(
    ExternalFunctionRegistry Functions,
    IReadOnlyList<string> NativeLibraries,
    IReadOnlyList<string>? BinaryLibraries = null,
    string? BinaryProfile = null,
    IReadOnlyList<BinaryBinding>? BinaryBindings = null,
    IDisposable? Staging = null,
    IReadOnlyList<BinaryLibrarySpec>? LibrarySpecs = null,
    string? BinaryTarget = null,
    IReadOnlyList<string>? CatalogInputs = null) : IDisposable
{
    public void Dispose() => Staging?.Dispose();
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class DirectoryStaging : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("blitzcs-binding-").FullName;
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
