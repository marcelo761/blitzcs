using System.Diagnostics;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Opt-in renderer counters (BLITZCS_ENGINE_PROFILE=1). Every call site is guarded by <see cref="Enabled"/>.</summary>
public static class D3D11FrameCounters
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE") == "1";

    public const int MeshDraws = 0, Triangles = 1, BlitDraws = 2, MipGenerations = 3, DynamicVertexUploads = 4,
        TextureUploads = 5, Readbacks = 6, ConstantBufferMaps = 7, Resolves = 8, ClearPipelineStates = 9,
        MipTicks = 10, DynamicUploadTicks = 11, TextureUploadTicks = 12, ReadbackTicks = 13, PresentTicks = 14, Count = 15;

    public static readonly long[] Values = new long[Count];

    public static void Add(int index, long amount = 1) => Values[index] += amount;
    public static long Now() => Stopwatch.GetTimestamp();
    public static void AddSince(int index, long start) => Values[index] += Stopwatch.GetTimestamp() - start;
}
