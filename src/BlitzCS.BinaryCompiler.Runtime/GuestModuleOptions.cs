namespace BlitzCS.BinaryCompiler.Runtime;

public sealed class GuestModuleOptions
{
    /// <summary>Usable bytes of each guest stack (a guard page is added below). Default 1 MiB.</summary>
    public int StackSize { get; init; } = 1024 * 1024;

    /// <summary>Headroom that must remain below a new call frame; calls that would leave less throw <see cref="GuestStackException"/>.</summary>
    public int StackReserve { get; init; } = 32 * 1024;

    /// <summary>Fill the words above each call frame with a pattern and verify it after the call (detects callees writing above their declared arguments).</summary>
    public bool CheckStackCanary { get; init; } = true;

    /// <summary>Load despite Error diagnostics in the artifact. Default false: unsupported input never loads silently.</summary>
    public bool AllowTranslationErrors { get; init; }

    /// <summary>
    /// Bind imports that the resolver does not provide to a faulting stub instead of failing the load. Calling such an import aborts the
    /// guest call with a <see cref="GuestImportException"/> naming dll!name, so a missing host service is never silently skipped.
    /// </summary>
    public bool AllowUnboundImports { get; init; }

    /// <summary>Run DllMain(DLL_PROCESS_ATTACH/DETACH) for DLLs with a non-zero entry point.</summary>
    public bool RunEntryPoint { get; init; } = true;

    /// <summary>
    /// What the guest's PEB describes about the process (ProcessParameters, OS version fields). Null: an anonymous process with
    /// empty image path / command line / environment and Windows XP SP3 (5.1.2600) version fields. See docs/binary-teb-contract.md.
    /// </summary>
    public GuestProcessInfo? Process { get; init; }
}

/// <summary>
/// Process identity a host wants the guest PEB to carry: the 32-bit RTL_USER_PROCESS_PARAMETERS strings and the OS version fields of the PEB.
/// The Win32 component builds one from its options (<c>Win32ProcessInfo.From</c>) so the PEB, GetCommandLineW and GetVersionEx agree.
/// </summary>
public sealed record GuestProcessInfo(
    string ImagePath,
    string CommandLine,
    IReadOnlyList<string> Environment,
    uint OsMajor = 5, uint OsMinor = 1, ushort OsBuild = 2600, ushort OsServicePack = 0x0300,
    string CurrentDirectory = "C:\\", uint StandardInput = 0, uint StandardOutput = 0, uint StandardError = 0)
{
    /// <summary>The OS version fields only (the strings stay empty).</summary>
    public static GuestProcessInfo Default { get; } = new("", "", []);
}
