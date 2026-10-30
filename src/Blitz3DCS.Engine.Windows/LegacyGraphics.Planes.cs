using System.Numerics;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    public static int CreatePlane(int segments = 1, int parent = 0)
    {
        if (segments is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(segments));
        CheckThreadIfStarted();
        var entity = CreateMesh(parent);
        try
        {
            CreateSurface(entity);
            GetMesh(entity).Plane = new PlaneState(segments);
            RequireScene().SetInfinitePlane(entity);
            return entity;
        }
        catch { FreeEntity(entity); throw; }
    }

    private static bool PreparePlane(MeshEntity mesh, Matrix4x4 world, Matrix4x4 cameraWorld, CameraState camera)
    {
        if (!Matrix4x4.Invert(world, out var inverse)) return false;
        var localCamera = cameraWorld * inverse;
        var viewport = camera.Viewport ?? (0, 0, _width, _height);
        var halfWidth = camera.Far / camera.Zoom;
        var halfHeight = halfWidth * viewport.Height / viewport.Width;
        var grid = mesh.Plane!.Grid;
        grid.Build(Vector3.Transform(Vector3.Zero, localCamera),
            Vector3.Transform(new Vector3(-halfWidth, halfHeight, camera.Far), localCamera),
            Vector3.Transform(new Vector3(halfWidth, halfHeight, camera.Far), localCamera),
            Vector3.Transform(new Vector3(halfWidth, -halfHeight, camera.Far), localCamera),
            Vector3.Transform(new Vector3(-halfWidth, -halfHeight, camera.Far), localCamera));
        return grid.IndexCount != 0;
    }

    private static D3D11Mesh PlaneGpuMesh(D3D11Renderer renderer, PlaneState plane)
    {
        if (plane.GpuMesh is null)
            plane.GpuMesh = renderer.CreateDynamicMesh(plane.Grid.Vertices, plane.Grid.Indices);
        else renderer.UpdateMeshVertices(plane.GpuMesh, plane.Grid.Vertices);
        return plane.GpuMesh;
    }

    private static void ClosePlaneGpuMeshes()
    {
        foreach (var mesh in MeshEntities.Values)
            if (mesh.Plane is { } plane)
            {
                plane.GpuMesh?.Dispose();
                plane.GpuMesh = null;
            }
    }

    private sealed class PlaneState(int segments)
    {
        internal int Segments { get; } = segments;
        internal PlaneGrid Grid { get; } = new(segments);
        internal D3D11Mesh? GpuMesh { get; set; }
    }
}
