using System.Diagnostics;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Opt-in renderer counters (BLITZCS_ENGINE_PROFILE=1). Every call site is guarded by <see cref="Enabled"/>.</summary>
public static class D3D11FrameCounters
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("BLITZCS_ENGINE_PROFILE") == "1";

    public const int MeshDraws = 0, Triangles = 1, BlitDraws = 2, MipGenerations = 3, DynamicVertexUploads = 4,
        TextureUploads = 5, Readbacks = 6, ConstantBufferMaps = 7, Resolves = 8, ClearPipelineStates = 9,
        MipTicks = 10, DynamicUploadTicks = 11, TextureUploadTicks = 12, ReadbackTicks = 13, PresentTicks = 14,
        SkippedBinds = 15, BatchedBlitOps = 16, SkippedConstantBufferMaps = 17,
        // Object-constant submission (additive): device draw calls actually issued for meshes, instanced draws and the
        // queued draws folded into them, bytes written to the per-frame object buffer, *SetConstantBuffers1 calls, and
        // queue flushes (each flush maps the object buffer once).
        ApiDraws = 18, InstancedDraws = 19, InstancedObjects = 20, ObjectBufferBytes = 21, ConstantBufferBinds = 22,
        DrawQueueFlushes = 23,
        // Instancing diagnostics (additive): why a run of consecutive draws ended at the next draw (first failing
        // criterion: mesh, bound textures, samplers, pipeline state, anything else), the run-length histogram of the
        // device draws, and queued draws moved by the commutative (additive) grouping.
        InstRunBreakMesh = 24, InstRunBreakTex = 25, InstRunBreakSampler = 26, InstRunBreakState = 27, InstRunBreakOther = 28,
        InstRunLen1 = 29, InstRunLen2 = 30, InstRunLen3To4 = 31, InstRunLen5To8 = 32, InstRunLen9Plus = 33,
        ReorderedDraws = 34, Count = 35;

    public static readonly long[] Values = new long[Count];

    public static void Add(int index, long amount = 1) => Values[index] += amount;
    public static long Now() => Stopwatch.GetTimestamp();
    public static void AddSince(int index, long start) => Values[index] += Stopwatch.GetTimestamp() - start;
}
