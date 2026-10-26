using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using BlitzCS.BinaryCompiler;

namespace BlitzCS.CLI;

/// <summary>
/// Explicit compilation targets supported by the BlitzCS CLI pipeline.
/// BCSBRE binary recompilation is integrated only in 64-bit targets (<see cref="Win64"/> and <see cref="Linux64"/>)
/// for legacy 32-bit PE32 x86 binary modules, with <see cref="Win64"/> as the initial validated integration target.
/// </summary>
public enum CompilationTarget
{
    Win32,
    Linux32,
    Win64,
    Linux64
}

/// <summary>Detected binary format and architecture of a native or legacy library file.</summary>
public enum BinaryModuleFormat
{
    Unknown,
    Pe32X86,
    Pe32PlusX64,
    PeOther,
    Elf32X86,
    Elf64X64,
    ElfOther
}

/// <summary>How a binary dependency is resolved for a specific <see cref="CompilationTarget"/>.</summary>
public enum DependencyResolutionKind
{
    /// <summary>Direct native linking/loading (the binary matches the target OS and architecture).</summary>
    NativeDirect,
    /// <summary>Legacy 32-bit PE32 x86 module translated in-process via BCSBRE for a 64-bit target.</summary>
    BcsbreRecompilation
}

public readonly record struct BinaryInspectionResult(BinaryModuleFormat Format, string Description);

public static class TargetCapabilities
{
    private static readonly HashSet<string> WindowsHostBridgeDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "ddraw.dll",
        "dinput.dll",
        "dinput8.dll",
        "d3dim.dll",
        "d3dim700.dll",
        "d3dxof.dll",
        "dsound.dll"
    };

    public static bool TryParseTarget(string? value, out CompilationTarget target)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "win32" or "win-x86" or "windows-x86":
                target = CompilationTarget.Win32;
                return true;
            case "linux32" or "linux-x86":
                target = CompilationTarget.Linux32;
                return true;
            case "win64" or "windows-x64" or "win-x64" or "windows":
                target = CompilationTarget.Win64;
                return true;
            case "linux64" or "linux-x64" or "linux-sysv" or "linux":
                target = CompilationTarget.Linux64;
                return true;
            default:
                target = default;
                return false;
        }
    }

    public static CompilationTarget ParseTarget(string? value) =>
        TryParseTarget(value, out var target)
            ? target
            : throw new ArgumentException($"Unknown target '{value}'; expected Win32, Linux32, Win64 (windows-x64) or Linux64 (linux-x64).");

    public static string CanonicalName(this CompilationTarget target) => target switch
    {
        CompilationTarget.Win32 => "Win32",
        CompilationTarget.Linux32 => "Linux32",
        CompilationTarget.Win64 => "Win64",
        CompilationTarget.Linux64 => "Linux64",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public static string RuntimeIdentifier(this CompilationTarget target) => target switch
    {
        CompilationTarget.Win32 => "win-x86",
        CompilationTarget.Linux32 => "linux-x86",
        CompilationTarget.Win64 => "win-x64",
        CompilationTarget.Linux64 => "linux-x64",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public static bool IsWindows(this CompilationTarget target) =>
        target is CompilationTarget.Win32 or CompilationTarget.Win64;

    public static bool Is64Bit(this CompilationTarget target) =>
        target is CompilationTarget.Win64 or CompilationTarget.Linux64;

    /// <summary>
    /// BCSBRE is integrated only in 64-bit targets for legacy 32-bit binary modules that require translation.
    /// </summary>
    public static bool SupportsBinaryRecompilation(this CompilationTarget target) =>
        target is CompilationTarget.Win64 or CompilationTarget.Linux64;

    /// <summary>
    /// Windows x64 is the initial validated integration target for full BCSBRE + Win32/DirectX host-bridged execution.
    /// </summary>
    public static bool IsValidatedBinaryRecompilationTarget(this CompilationTarget target) =>
        target == CompilationTarget.Win64;

    public static string? BinaryCompilerTargetOs(this CompilationTarget target) => target switch
    {
        CompilationTarget.Win64 => BinaryTargets.WindowsX64,
        CompilationTarget.Linux64 => BinaryTargets.LinuxSysV,
        _ => null
    };

    public static BinaryModuleFormat ExpectedNativeFormat(this CompilationTarget target) => target switch
    {
        CompilationTarget.Win32 => BinaryModuleFormat.Pe32X86,
        CompilationTarget.Linux32 => BinaryModuleFormat.Elf32X86,
        CompilationTarget.Win64 => BinaryModuleFormat.Pe32PlusX64,
        CompilationTarget.Linux64 => BinaryModuleFormat.Elf64X64,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public static BinaryInspectionResult InspectBinary(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Binary dependency '{Path.GetFileName(path)}' does not exist.", path);

        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[20];
        var read = stream.Read(prefix);
        if (read < 4)
            return new(BinaryModuleFormat.Unknown, "truncated or unrecognized binary header");

        if (prefix[0] == 0x7f && prefix[1] == (byte)'E' && prefix[2] == (byte)'L' && prefix[3] == (byte)'F')
        {
            if (read < 20)
                return new(BinaryModuleFormat.ElfOther, "truncated ELF header");
            var elfClass = prefix[4];
            var dataEncoding = prefix[5];
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(prefix[18..20]);
            if (dataEncoding == 1 && elfClass == 1 && machine == 3)
                return new(BinaryModuleFormat.Elf32X86, "ELF32 x86");
            if (dataEncoding == 1 && elfClass == 2 && machine == 62)
                return new(BinaryModuleFormat.Elf64X64, "ELF64 x86-64");
            return new(BinaryModuleFormat.ElfOther, $"ELF (class={elfClass}, endian={dataEncoding}, machine=0x{machine:X4})");
        }

        if (prefix[0] == (byte)'M' && prefix[1] == (byte)'Z')
        {
            try
            {
                stream.Position = 0;
                using var pe = new PEReader(stream);
                var machine = pe.PEHeaders.CoffHeader.Machine;
                var magic = pe.PEHeaders.PEHeader?.Magic;
                if (machine == Machine.I386 && magic == PEMagic.PE32)
                    return new(BinaryModuleFormat.Pe32X86, "PE32 x86");
                if (machine == Machine.Amd64 && magic == PEMagic.PE32Plus)
                    return new(BinaryModuleFormat.Pe32PlusX64, "PE32+ x64");
                return new(BinaryModuleFormat.PeOther, $"PE (machine={machine}, magic={magic})");
            }
            catch (BadImageFormatException)
            {
                return new(BinaryModuleFormat.Unknown, "malformed PE header");
            }
        }

        return new(BinaryModuleFormat.Unknown, "unrecognized binary format");
    }

    /// <summary>
    /// Validates that a direct native library matches the target's native binary format and ABI.
    /// Native libraries retain their direct linking/loading path and never invoke BCSBRE.
    /// </summary>
    internal static void ValidateNativeLibrary(string path, CompilationTarget target)
    {
        var inspected = InspectBinary(path);
        var expected = target.ExpectedNativeFormat();
        if (inspected.Format == expected)
            return;

        var fileName = Path.GetFileName(path);
        throw target switch
        {
            CompilationTarget.Win64 => new ArgumentException(
                $"Native library '{fileName}' is not Windows x64. Declare legacy PE32 libraries in binaryLibraries for recompilation. (Detected {inspected.Description}.)"),
            CompilationTarget.Linux64 => new ArgumentException(
                $"Native library '{fileName}' is not Linux x64. (Detected {inspected.Description}.)"),
            CompilationTarget.Win32 => new ArgumentException(
                $"Native library '{fileName}' is not Windows x86 (Win32); detected {inspected.Description}. Native 32-bit targets require PE32 x86 libraries."),
            CompilationTarget.Linux32 => new ArgumentException(
                $"Native library '{fileName}' is not Linux x86 (Linux32); detected {inspected.Description}. Native 32-bit targets require ELF32 x86 libraries."),
            _ => new ArgumentOutOfRangeException(nameof(target))
        };
    }

    /// <summary>
    /// Validates a legacy binary module scheduled for BCSBRE recompilation against the target's capabilities.
    /// BCSBRE is only invoked on 64-bit targets for legacy PE32 x86 modules.
    /// </summary>
    internal static void ValidateLegacyBinaryForRecompilation(string path, CompilationTarget target, bool isPackageBuild = false)
    {
        var fileName = Path.GetFileName(path);
        if (!target.SupportsBinaryRecompilation())
        {
            throw new ArgumentException(
                $"Target '{target.CanonicalName()}' ({target.RuntimeIdentifier()}) is a 32-bit target; BCSBRE binary recompilation is only integrated in 64-bit targets (Win64). " +
                $"Library '{fileName}' cannot be translated for {target.CanonicalName()}; use nativeLibraries for direct 32-bit native linking.");
        }

        var inspected = InspectBinary(path);
        if (inspected.Format == target.ExpectedNativeFormat())
        {
            throw new ArgumentException(
                $"Binary library '{fileName}' is already a native 64-bit {inspected.Description} image for target '{target.CanonicalName()}' (is not a PE32 x86 image); " +
                "declare native 64-bit libraries in nativeLibraries so they retain their direct loading path without BCSBRE translation.");
        }

        if (inspected.Format != BinaryModuleFormat.Pe32X86)
        {
            throw new ArgumentException(
                $"Binary library '{fileName}' ({inspected.Description}) is not a PE32 x86 image.");
        }

        if (target == CompilationTarget.Linux64 && isPackageBuild)
        {
            throw new ArgumentException(
                $"Target 'Linux64' (linux-x64) is not a validated application packaging target for legacy PE32 binary libraries ('{fileName}'). " +
                "Keep Windows x64 (Win64 / windows-x64) as the target for packaged legacy PE32 binary dependencies.");
        }
    }

    /// <summary>
    /// Validates that a parsed PE32 image's imports are compatible with the target's host bridge capabilities.
    /// </summary>
    internal static void ValidateTargetImportCapabilities(string fileName, PeImage image, CompilationTarget target)
    {
        if (target != CompilationTarget.Linux64)
            return;

        var unsupportedHostImport = image.Imports.FirstOrDefault(i => WindowsHostBridgeDlls.Contains(i.Dll));
        if (unsupportedHostImport is not null)
        {
            var symbol = unsupportedHostImport.Name ?? $"#{unsupportedHostImport.Ordinal}";
            throw new InvalidOperationException(
                $"Binary library '{fileName}' requires Windows host bridge import '{unsupportedHostImport.Dll}!{symbol}', which is unsupported on target 'Linux64' (linux-x64). " +
                "Windows x64 (Win64 / windows-x64) is the validated target for Win32/DirectX host-bridged binary translation.");
        }
    }

    /// <summary>
    /// Resolves whether a binary dependency on <paramref name="target"/> should use direct native loading
    /// or BCSBRE binary recompilation, or throws an explicit diagnostic if unsupported.
    /// </summary>
    internal static DependencyResolutionKind ResolveDependency(string path, CompilationTarget target)
    {
        var inspected = InspectBinary(path);
        if (inspected.Format == target.ExpectedNativeFormat())
            return DependencyResolutionKind.NativeDirect;

        if (inspected.Format == BinaryModuleFormat.Pe32X86 && target.SupportsBinaryRecompilation())
            return DependencyResolutionKind.BcsbreRecompilation;

        var fileName = Path.GetFileName(path);
        if (!target.SupportsBinaryRecompilation() && inspected.Format == BinaryModuleFormat.Pe32X86)
        {
            throw new ArgumentException(
                $"Dependency '{fileName}' is a PE32 x86 binary, which cannot be loaded on target '{target.CanonicalName()}' ({target.RuntimeIdentifier()}); " +
                "BCSBRE binary recompilation is only integrated in 64-bit targets (Win64).");
        }

        throw new ArgumentException(
            $"Dependency '{fileName}' has unsupported binary format '{inspected.Description}' for target '{target.CanonicalName()}' ({target.RuntimeIdentifier()}).");
    }
}
