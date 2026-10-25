namespace BlitzCS.BinaryCompiler.CodeGen;

/// <summary>Host calling convention the runtime routines (EnterGuest, import stubs) are generated for. Translated guest code is identical for both.</summary>
internal enum HostAbi { SysV, Win64 }

internal static class HostAbis
{
    internal static HostAbi For(string targetOs) => BinaryTargets.Normalize(targetOs) switch
    {
        BinaryTargets.WindowsX64 => HostAbi.Win64,
        _ => HostAbi.SysV,
    };
}
