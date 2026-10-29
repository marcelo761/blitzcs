using Blitz3DCS.Engine;
using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    public static void UpdateWorld(float elapsed = 1)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        UpdateAnimations(scene, elapsed);
        PrepareCollisionGeometry(scene);
        scene.UpdateWorld(elapsed);
    }

    private static void PrepareCollisionGeometry(SceneGraph scene)
    {
        foreach (var mesh in MeshEntities.Values)
        {
            if (!scene.RequiresPolygonCollision(mesh.Entity)) continue;
            var geometry = mesh.Geometry;
            var version = GeometryVersion(geometry);
            if (geometry.CollisionVersion != version)
            {
                var count = 0;
                foreach (var handle in geometry.SurfaceHandles) count += Surfaces[handle].Indices.Count / 3;
                var triangles = new SceneCollisionTriangle[count];
                var next = 0;
                foreach (var handle in geometry.SurfaceHandles)
                {
                    var surface = Surfaces[handle];
                    for (var i = 0; i < surface.Indices.Count; i += 3)
                    {
                        triangles[next++] = new SceneCollisionTriangle(
                            ToSceneVector(surface.Vertices[(int)surface.Indices[i]].Position),
                            ToSceneVector(surface.Vertices[(int)surface.Indices[i + 1]].Position),
                            ToSceneVector(surface.Vertices[(int)surface.Indices[i + 2]].Position), i / 3, handle);
                    }
                }
                geometry.CollisionTriangles = triangles;
                geometry.CollisionVersion = version;
            }
            if (mesh.CollisionVersion == version) continue;
            scene.SetCollisionTriangles(mesh.Entity, geometry.CollisionTriangles);
            mesh.CollisionVersion = version;
        }
    }

    private static SceneVector3 ToSceneVector(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
}
