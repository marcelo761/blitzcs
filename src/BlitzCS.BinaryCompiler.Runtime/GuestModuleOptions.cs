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

    /// <summary>Run DllMain(DLL_PROCESS_ATTACH/DETACH) for DLLs with a non-zero entry point.</summary>
    public bool RunEntryPoint { get; init; } = true;
}
