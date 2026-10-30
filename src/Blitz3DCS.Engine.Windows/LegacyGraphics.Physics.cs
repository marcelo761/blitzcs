using Blitz3DCS.Engine;
using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static SceneGraph? _collisionGeometrySelectionScene;
    private static long _collisionGeometrySelectionVersion = long.MinValue;
    private static int _collisionGeometryMeshCount = -1;
    private static readonly List<MeshEntity> PolygonCollisionMeshes = [];

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

    private static readonly List<MeshGeometry> PendingCollisionGeometries = [];
    private static bool _collisionFullPassNeeded = true;
    private static long _collisionPrepMeshTotal, _collisionPrepRebuiltTotal;

    internal static long CollisionPrepMeshTotalForTesting => _collisionPrepMeshTotal;
    internal static long CollisionPrepRebuiltTotalForTesting => _collisionPrepRebuiltTotal;

    /// <summary>A surface of this geometry changed: queue it for re-preparation if it is collision-relevant.</summary>
    private static void MarkGeometryChanged(MeshGeometry geometry)
    {
        if (!geometry.InCollisionSet || geometry.CollisionPending) return;
        geometry.CollisionPending = true;
        PendingCollisionGeometries.Add(geometry);
    }

    private static void ClearPendingCollision()
    {
        foreach (var geometry in PendingCollisionGeometries) geometry.CollisionPending = false;
        PendingCollisionGeometries.Clear();
    }

    private static void PrepareCollisionGeometry(SceneGraph scene)
    {
        var selectionVersion = scene.PolygonGeometrySelectionVersion;
        var meshCount = MeshEntities.Count;
        if (!ReferenceEquals(_collisionGeometrySelectionScene, scene) ||
            _collisionGeometrySelectionVersion != selectionVersion ||
            _collisionGeometryMeshCount != meshCount)
        {
            ResetCollisionSet();
            foreach (var mesh in MeshEntities.Values)
            {
                if (mesh.Plane is not null || !scene.RequiresPolygonCollision(mesh.Entity)) continue;
                PolygonCollisionMeshes.Add(mesh);
                mesh.Geometry.InCollisionSet = true;
            }
            EngineStats.Add(EngineStats.ColPrepSel);
            _collisionGeometrySelectionScene = scene;
            _collisionGeometrySelectionVersion = selectionVersion;
            _collisionGeometryMeshCount = meshCount;
            _collisionFullPassNeeded = true;
        }
        var optimized = !EngineOptimizations.Disabled;
        // Incremental: only geometries whose own surfaces changed since the last prepare need a look.
        if (optimized && !_collisionFullPassNeeded && PendingCollisionGeometries.Count == 0) return;
        var full = _collisionFullPassNeeded || !optimized;

        foreach (var mesh in PolygonCollisionMeshes)
        {
            if (!full && !mesh.Geometry.CollisionPending) continue;
            PrepareCollisionMesh(scene, mesh);
        }
        ClearPendingCollision();
        _collisionFullPassNeeded = false;
    }

    private static void PrepareCollisionMesh(SceneGraph scene, MeshEntity mesh)
    {
        EngineStats.Add(EngineStats.ColPrepMeshes);
        _collisionPrepMeshTotal++;
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
            _collisionPrepRebuiltTotal++;
            geometry.CollisionTriangles = triangles;
            geometry.CollisionVersion = version;
        }
        if (mesh.CollisionVersion == version) return;
        scene.SetCollisionTriangles(mesh.Entity, geometry.CollisionTriangles);
        mesh.CollisionVersion = version;
    }

    private static void ResetCollisionSet()
    {
        foreach (var mesh in PolygonCollisionMeshes) mesh.Geometry.InCollisionSet = false;
        PolygonCollisionMeshes.Clear();
        ClearPendingCollision();
    }

    /// <summary>Test oracle: number of collision-set meshes whose prepared triangles/version differ from what a full scan would produce now.</summary>
    internal static int CollisionStaleMeshCountForTesting()
    {
        var stale = 0;
        foreach (var mesh in PolygonCollisionMeshes)
        {
            var geometry = mesh.Geometry;
            long version = geometry.SurfaceHandles.Count;
            foreach (var handle in geometry.SurfaceHandles) version += Surfaces[handle].Version;
            if (geometry.CollisionVersion != version || mesh.CollisionVersion != version) stale++;
        }
        return stale;
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
        ResetCollisionSet();
        _collisionGeometrySelectionScene = null;
        _collisionGeometrySelectionVersion = long.MinValue;
        _collisionGeometryMeshCount = -1;
        _collisionFullPassNeeded = true;
    }

    private static SceneVector3 ToSceneVector(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
}
