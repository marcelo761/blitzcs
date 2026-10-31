using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    public static int LinePick(float x, float y, float z, float dx, float dy, float dz, float radius = 0)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        PrepareCollisionGeometry(scene);
        return scene.LinePick(x, y, z, dx, dy, dz, radius);
    }

    public static int EntityPick(int entity, float range)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        PrepareCollisionGeometry(scene);
        return scene.EntityPick(entity, range);
    }

    public static int EntityVisible(int source, int destination)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        PrepareCollisionGeometry(scene);
        return scene.EntityVisible(source, destination) ? 1 : 0;
    }

    public static int CameraPick(int camera, float x, float y)
    {
        CheckThreadIfStarted();
        var state = GetCamera(camera);
        var viewport = state.Viewport ?? (0, 0, _width, _height);
        var width = state.Near * 2 / state.Zoom;
        var height = width * viewport.Height / viewport.Width;
        x = (x / viewport.Width - .5f) * width;
        y = (.5f - y / viewport.Height) * height;
        var world = ToWorldMatrix(RequireScene().GetWorldTransform(camera));
        var origin = state.ProjectionMode == 2 ? new Vector3(x, y, 0) : Vector3.Zero;
        var direction = state.ProjectionMode == 2 ? new Vector3(0, 0, state.Far) :
            new Vector3(x * state.Far / state.Near, y * state.Far / state.Near, state.Far);
        origin = Vector3.Transform(origin, world);
        direction = Vector3.TransformNormal(direction, world);
        return LinePick(origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z);
    }
}
