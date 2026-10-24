using System.Diagnostics;
using System.Xml.Linq;
using System.Reflection.PortableExecutable;

namespace BlitzCS.CLI;

/// <summary>Packages Roslyn output with the SDK's apphost and self-contained runtime publisher.</summary>
internal static class ApplicationPublisher
{
    internal static void Build(string generated, string destination, string name,
        string target, ExternalCatalogResult catalog, IEnumerable<string> inputs, TextWriter output,
        TextWriter error, string? assets, bool graphical)
    {
        var windows = target == "windows-x64";
        var externalFiles = catalog.Functions.ReferencePaths.Concat(catalog.NativeLibraries).Distinct().ToArray();
        if (windows && externalFiles.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("Windows dependencies must have distinct filenames regardless of case.");
        var header = new byte[20];
        foreach (var native in catalog.NativeLibraries.Except(catalog.BinaryLibraries ?? []))
        {
            using var stream = File.OpenRead(native);
            if (windows)
            {
                using var pe = new PEReader(stream);
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus)
                    throw new ArgumentException($"Native library '{Path.GetFileName(native)}' is not Windows x64. Declare legacy PE32 libraries in binaryLibraries for recompilation.");
            }
            else
            {
                if (stream.Read(header) != 20 || !header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 }) || header[4] != 2 || header[18] != 62)
                    throw new ArgumentException($"Native library '{Path.GetFileName(native)}' is not Linux x64.");
            }
        }
        var destinationPath = DotNetEmitter.CanonicalPath(destination);
        const string ownershipFile = ".blitzcs-package";
        if (File.Exists(destinationPath)) throw new ArgumentException("Package destination must be a directory.");
        var replacing = Directory.Exists(destinationPath);
        if (replacing && (new DirectoryInfo(destinationPath).LinkTarget is not null ||
            !File.Exists(Path.Combine(destinationPath, ownershipFile)) ||
            File.ReadAllText(Path.Combine(destinationPath, ownershipFile)) != "BlitzCS application package v1\n"))
            throw new ArgumentException($"Package destination '{destinationPath}' already exists and is not a BlitzCS package.");
        foreach (var input in inputs.Concat(catalog.Functions.ReferencePaths).Concat(catalog.NativeLibraries))
            if (DotNetEmitter.CanonicalPath(input).StartsWith(destinationPath + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("Package destination contains a compiler input.");
        if (assets is not null)
        {
            var assetRoot = DotNetEmitter.CanonicalPath(assets).TrimEnd(Path.DirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (assetRoot.Equals(destinationPath, comparison) ||
                assetRoot.StartsWith(destinationPath + Path.DirectorySeparatorChar, comparison) ||
                destinationPath.StartsWith(assetRoot + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Asset directory and package destination must not overlap.");
        }
        // Publishing into a temporary directory keeps restore/compiler failures away from existing outputs.
        var workspace = Path.Combine(Path.GetTempPath(), "blitzcs-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var stage = Path.Combine(workspace, "input");
            var assemblyPath = Path.Combine(stage, name + ".dll");
            var binaries = BinaryTranslationStep.Prepare(catalog, assemblyPath, error, target);
            const string startup = """
                internal static class BlitzStartup
                {
                    [System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Initialize() => System.IO.Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);
                }
                """;
            DotNetEmitter.Emit(generated + Environment.NewLine + startup, assemblyPath, "BlitzProgram", catalog.Functions.ReferencePaths,
                catalog.NativeLibraries, inputs);
            binaries.Publish(output);
            // Publish a shell to obtain the SDK's apphost, runtimeconfig and dependency graph.
            // Keep the original Roslyn assembly: generated binding assemblies can reference runtime
            // implementation types, which cannot be recompiled against SDK reference-pack facades.
            File.WriteAllText(Path.Combine(stage, "PublishEntry.cs"),
                "internal static class BlitzProgram { public static void Main() {} }");
            var properties = new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("LangVersion", "14.0"),
                new XElement("OutputType", graphical ? "WinExe" : "Exe"),
                new XElement("AssemblyName", name),
                new XElement("StartupObject", "BlitzProgram"),
                new XElement("EnableDefaultItems", "false"),
                new XElement("GenerateAssemblyInfo", "false"),
                new XElement("UseAppHost", "true"),
                new XElement("SelfContained", "true"),
                new XElement("PublishTrimmed", "false"),
                new XElement("PublishSingleFile", "false"));
            var items = new XElement("ItemGroup",
                new XElement("Compile", new XAttribute("Include", "PublishEntry.cs")));
            foreach (var file in Directory.EnumerateFiles(stage))
            {
                if (file == assemblyPath || file.EndsWith(".cs", StringComparison.Ordinal) ||
                    file == Path.ChangeExtension(assemblyPath, ".runtimeconfig.json") ||
                    file == Path.ChangeExtension(assemblyPath, ".pdb")) continue;
                // Managed reference closure was already discovered by the emitter. Native DLLs are content.
                var managed = false;
                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    try { _ = System.Reflection.AssemblyName.GetAssemblyName(file); managed = true; }
                    catch (BadImageFormatException) { }
                }
                items.Add(managed
                    ? new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(file)),
                        new XElement("HintPath", file), new XElement("Private", "true"))
                    : new XElement("Content", new XAttribute("Include", file),
                        new XElement("CopyToPublishDirectory", "Always")));
            }
            var projectPath = Path.Combine(stage, "Application.csproj");
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), properties, items)).Save(projectPath);
            var package = Path.Combine(workspace, "package");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in new[] { "publish", projectPath, "-c", "Release", "-r",
                         target == "windows-x64" ? "win-x64" : "linux-x64", "--self-contained", "true", "-o", package, "--nologo" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the .NET SDK publisher.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(300_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new InvalidOperationException(".NET SDK publishing exceeded five minutes.");
            }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($".NET SDK publishing failed ({process.ExitCode}). Install the .NET 10 SDK and restore access to the target runtime packs.{Environment.NewLine}{stdout.Result}{stderr.Result}");
            File.Copy(assemblyPath, Path.Combine(package, name + ".dll"), overwrite: true);
            File.Copy(Path.ChangeExtension(assemblyPath, ".pdb"), Path.Combine(package, name + ".pdb"), overwrite: true);
            if (assets is not null)
            {
                var packagedNames = new HashSet<string>(Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(package, path)), windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                packagedNames.Add(ownershipFile);
                var root = Path.GetFullPath(assets);
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Asset directory '{root}' does not exist.");
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var assetTarget = Path.Combine(package, Path.GetRelativePath(root, file));
                    if (!packagedNames.Add(Path.GetRelativePath(root, file))) throw new ArgumentException($"Asset '{Path.GetRelativePath(root, file)}' conflicts with a published dependency.");
                    Directory.CreateDirectory(Path.GetDirectoryName(assetTarget)!);
                    File.Copy(file, assetTarget);
                }
            }
            File.WriteAllText(Path.Combine(package, ownershipFile), "BlitzCS application package v1\n");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var stagedDestination = destinationPath + ".blitzcs-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(stagedDestination);
                foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
                {
                    var copied = Path.Combine(stagedDestination, Path.GetRelativePath(package, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                    File.Copy(file, copied);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copied, File.GetUnixFileMode(file));
                }
                var backup = destinationPath + ".previous-" + Guid.NewGuid().ToString("N");
                if (replacing) Directory.Move(destinationPath, backup);
                try { Directory.Move(stagedDestination, destinationPath); }
                catch
                {
                    if (replacing) Directory.Move(backup, destinationPath);
                    throw;
                }
                if (replacing) Directory.Delete(backup, true);
            }
            finally { if (Directory.Exists(stagedDestination)) Directory.Delete(stagedDestination, true); }
            output.WriteLine(Path.Combine(destinationPath, name + (target == "windows-x64" ? ".exe" : "")));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
}
