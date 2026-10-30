namespace Blitz3DCS.Engine;

/// <summary>Runtime switch for CPU optimizations. Set BLITZCS_ENGINE_NOOPT=1 to restore the original code paths.</summary>
public static class EngineOptimizations
{
    /// <summary>True when BLITZCS_ENGINE_NOOPT=1 was set at process start (read once).</summary>
    public static readonly bool Disabled = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_NOOPT") == "1";
}
