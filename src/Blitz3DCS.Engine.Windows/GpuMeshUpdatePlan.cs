namespace Blitz3DCS.Engine.Windows;

internal enum GpuMeshAction { CreateImmutable, UpdateInPlace, CreateResizable }

/// <summary>
/// Chooses how a changed surface reaches the GPU. The first upload stays immutable (most surfaces never change
/// again); a surface that changes while it already has a GPU mesh is rewritten every frame (particles, dynamic
/// geometry), so it moves to a resizable dynamic mesh that is updated in place while the new geometry fits.
/// </summary>
internal readonly record struct GpuMeshUpdatePlan(GpuMeshAction Action, int VertexCapacity, uint IndexCapacity)
{
    private const int MinimumVertexCapacity = 32;

    internal static GpuMeshUpdatePlan Decide(bool hasMesh, bool meshResizable, int vertexCapacity,
        uint indexCapacity, int vertexCount, int indexCount)
    {
        if (!hasMesh) return new(GpuMeshAction.CreateImmutable, 0, 0);
        if (meshResizable && vertexCount <= vertexCapacity && (uint)indexCount <= indexCapacity)
            return new(GpuMeshAction.UpdateInPlace, vertexCapacity, indexCapacity);
        var vertices = Math.Max(MinimumVertexCapacity, (int)RoundUp((uint)Math.Max(vertexCount, 1) + (uint)Math.Max(vertexCount, 1) / 2));
        var indices = Math.Max((uint)vertices * 3u, RoundUp((uint)Math.Max(indexCount, 1) + (uint)Math.Max(indexCount, 1) / 2));
        return new(GpuMeshAction.CreateResizable, vertices, indices);
    }

    private static uint RoundUp(uint value) => System.Numerics.BitOperations.RoundUpToPowerOf2(value);
}
