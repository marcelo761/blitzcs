using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static Vector3 _projectedPoint;

    public static void CameraClsMode(int camera, int color, int zBuffer)
    {
        CheckThreadIfStarted();
        var state = GetCamera(camera);
        state.ClearColorEnabled = color != 0;
        state.ClearDepthEnabled = zBuffer != 0;
    }

    public static void CameraProject(int camera, float x, float y, float z)
    {
        CheckThreadIfStarted();
        var state = GetCamera(camera);
        var viewport = state.Viewport ?? (0, 0, _width, _height);
        var world = ToWorldMatrix(RequireScene().GetWorldTransform(camera));
        if (!Matrix4x4.Invert(world, out var view)) throw new InvalidOperationException("The camera transform is not invertible.");
        var point = Vector3.Transform(new Vector3(x, y, z), view);
        _projectedPoint = default;
        if (state.ProjectionMode != 2 && !(point.Z > 0 && point.Z <= state.Far)) return;
        var width = state.Near * 2 / state.Zoom;
        var height = width * viewport.Height / viewport.Width;
        var scale = state.ProjectionMode == 2 ? 1 : state.Near / point.Z;
        // The original query returns viewport-relative pixels and near-plane Z.
        _projectedPoint = new Vector3((point.X * scale / width + .5f) * viewport.Width,
            (.5f - point.Y * scale / height) * viewport.Height, state.Near);
    }

    public static float ProjectedX() => _projectedPoint.X;
    public static float ProjectedY() => _projectedPoint.Y;
    public static float ProjectedZ() => _projectedPoint.Z;

    public static void MeshCullBox(int mesh, float x, float y, float z, float width, float height, float depth)
    {
        CheckThreadIfStarted();
        var geometry = GetMesh(mesh).Geometry;
        geometry.CullBox = width < 0 || height < 0 || depth < 0 ? null :
            (new Vector3(x, y, z), new Vector3(x + width, y + height, z + depth));
    }

    public static int EntityInView(int entity, int camera)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        var state = GetCamera(camera);
        var viewport = state.Viewport ?? (0, 0, _width, _height);
        var cameraWorld = ToWorldMatrix(scene.GetWorldTransform(camera));
        if (!Matrix4x4.Invert(cameraWorld, out var view)) throw new InvalidOperationException("The camera transform is not invertible.");
        // EntityInView queries the camera's perspective frustum even in orthographic mode.
        var projection = CreateLeftHandedPerspectiveFromScales(state.Zoom,
            state.Zoom * viewport.Width / viewport.Height, state.Near, state.Far);
        var matrix = ToWorldMatrix(scene.GetWorldTransform(entity)) * view * projection;
        if (MeshEntities.TryGetValue(entity, out var mesh) && mesh.Plane is null && !Sprites.ContainsKey(entity))
        {
            EnsureGeometryBounds(mesh.Geometry);
            return mesh.Geometry.HasBounds && BoxIntersectsClipVolume(mesh.Geometry.BoundsMinimum, mesh.Geometry.BoundsMaximum, matrix) ? 1 : 0;
        }
        return BoxIntersectsClipVolume(Vector3.Zero, Vector3.Zero, matrix) ? 1 : 0;
    }
}
