using Blitz3DCS.Engine;
using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static SceneGraph? _collisionGeometrySelectionScene;
    private static long _collisionGeometrySelectionVersion = long.MinValue;
    private static int _collisionGeometryMeshCount = -1;
    private static readonly List<MeshEntity> PolygonCollisionMeshes = [];
    private static long _collisionPreparedEpoch = -1;

    internal static int PolygonCollisionMeshCountForTesting => PolygonCollisionMeshes.Count;

    public static void UpdateWorld(float elapsed = 1)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        var t0 = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        UpdateAnimations(scene, elapsed);
        var t1 = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        PrepareCollisionGeometry(scene);
        var t2 = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        scene.UpdateWorld(elapsed);
        if (EngineProfile.Enabled)
        {
            var t3 = EngineProfile.Now();
            EngineProfile.AddTicks(EngineProfile.UpdateAnimation, t1 - t0);
            EngineProfile.AddTicks(EngineProfile.UpdateCollision, t2 - t1);
            EngineProfile.AddTicks(EngineProfile.UpdateScene, t3 - t2);
            EngineProfile.AddTicks(EngineProfile.UpdateWorld, t3 - t0);
        }
    }

    private static void PrepareCollisionGeometry(SceneGraph scene)
    {
        var selectionVersion = scene.PolygonGeometrySelectionVersion;
        var meshCount = MeshEntities.Count;
        if (!ReferenceEquals(_collisionGeometrySelectionScene, scene) ||
            _collisionGeometrySelectionVersion != selectionVersion ||
            _collisionGeometryMeshCount != meshCount)
        {
            PolygonCollisionMeshes.Clear();
            foreach (var mesh in MeshEntities.Values)
            {
                if (mesh.Plane is not null || !scene.RequiresPolygonCollision(mesh.Entity)) continue;
                PolygonCollisionMeshes.Add(mesh);
            }
            EngineStats.Add(EngineStats.ColPrepSel);
            _collisionGeometrySelectionScene = scene;
            _collisionGeometrySelectionVersion = selectionVersion;
            _collisionGeometryMeshCount = meshCount;
            _collisionPreparedEpoch = -1;
        }
        // No surface changed since the last full pass and the mesh selection is unchanged: nothing to refresh.
        if (!EngineOptimizations.Disabled && _collisionPreparedEpoch == SurfaceState.MutationEpoch) return;

        foreach (var mesh in PolygonCollisionMeshes)
        {
            EngineStats.Add(EngineStats.ColPrepMeshes);
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
                EngineStats.Add(EngineStats.ColPrepRebuilt);
                geometry.CollisionTriangles = triangles;
                geometry.CollisionVersion = version;
            }
            if (mesh.CollisionVersion == version) continue;
            scene.SetCollisionTriangles(mesh.Entity, geometry.CollisionTriangles);
            mesh.CollisionVersion = version;
        }
        _collisionPreparedEpoch = SurfaceState.MutationEpoch;
    }

    /// <summary>One-line [scene] census (profiling only): entity totals by kind and collision-typed entities per type id.</summary>
    private static string BuildSceneCensus(SceneGraph scene)
    {
        var types = new SortedDictionary<int, int>();
        var entities = scene.CountEntitiesAndCollisionTypes(types);
        var text = new System.Text.StringBuilder(160);
        text.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"[scene] entities={entities} meshes={MeshEntities.Count} animated={AnimatedModels.Count} lights={LightEntities.Count} cameras={Cameras.Count} sprites={Sprites.Count} colTyped={types.Values.Sum()}");
        foreach (var pair in types) text.Append(System.Globalization.CultureInfo.InvariantCulture, $" colType{pair.Key}={pair.Value}");
        return text.ToString();
    }

    private static void ClearCollisionGeometrySelectionCache()
    {
        PolygonCollisionMeshes.Clear();
        _collisionGeometrySelectionScene = null;
        _collisionGeometrySelectionVersion = long.MinValue;
        _collisionGeometryMeshCount = -1;
        _collisionPreparedEpoch = -1;
    }

    private static SceneVector3 ToSceneVector(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
}
