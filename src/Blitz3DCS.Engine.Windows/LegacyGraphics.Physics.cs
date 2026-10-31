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

    private static long _collisionSelectionRebuildTotal;
    internal static long CollisionSelectionRebuildTotalForTesting => _collisionSelectionRebuildTotal;
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

    private static readonly HashSet<int> PendingSelectionMeshes = [];
    private static readonly List<int> SelectionChangeBuffer = [];

    private static void NoteMeshAdded(int entity)
    {
        EngineStats.Add(EngineStats.SelBumpCount);
        if (!EngineOptimizations.Disabled) PendingSelectionMeshes.Add(entity);
    }

    private static void NoteMeshRemoved(MeshEntity mesh)
    {
        EngineStats.Add(EngineStats.SelBumpCount);
        if (EngineOptimizations.Disabled) return;
        PendingSelectionMeshes.Remove(mesh.Entity);
        DeselectMesh(mesh);
    }

    private static void SelectMesh(MeshEntity mesh)
    {
        mesh.SelectionIndex = PolygonCollisionMeshes.Count;
        PolygonCollisionMeshes.Add(mesh);
        mesh.Geometry.SelectedMeshes.Add(mesh);
        mesh.Geometry.InCollisionSet = true;
    }

    private static void DeselectMesh(MeshEntity mesh)
    {
        var index = mesh.SelectionIndex;
        if (index < 0) return;
        var last = PolygonCollisionMeshes.Count - 1;
        var moved = PolygonCollisionMeshes[last];
        PolygonCollisionMeshes[index] = moved;
        moved.SelectionIndex = index;
        PolygonCollisionMeshes.RemoveAt(last);
        mesh.SelectionIndex = -1;
        var shared = mesh.Geometry.SelectedMeshes;
        shared.Remove(mesh);
        if (shared.Count == 0) mesh.Geometry.InCollisionSet = false;
    }

    /// <summary>Re-evaluates one mesh against the full-rebuild predicate and adjusts the selected set.</summary>
    private static void ReevaluateSelection(SceneGraph scene, MeshEntity mesh)
    {
        var wanted = mesh.Plane is null && scene.RequiresPolygonCollision(mesh.Entity);
        var selected = mesh.SelectionIndex >= 0;
        if (wanted == selected) return;
        if (wanted)
        {
            SelectMesh(mesh);
            PrepareCollisionMesh(scene, mesh);
        }
        else DeselectMesh(mesh);
    }

    private static void PrepareCollisionGeometry(SceneGraph scene)
    {
        var selectionVersion = scene.PolygonGeometrySelectionVersion;
        var optimized = !EngineOptimizations.Disabled;
        var meshCount = MeshEntities.Count;
        if (!ReferenceEquals(_collisionGeometrySelectionScene, scene) ||
            _collisionGeometrySelectionVersion != selectionVersion ||
            (!optimized && _collisionGeometryMeshCount != meshCount))
        {
            ResetCollisionSet();
            SelectionChangeBuffer.Clear();
            scene.TakeSelectionChanges(SelectionChangeBuffer);
            SelectionChangeBuffer.Clear();
            PendingSelectionMeshes.Clear();
            foreach (var mesh in MeshEntities.Values)
            {
                if (mesh.Plane is not null || !scene.RequiresPolygonCollision(mesh.Entity)) continue;
                SelectMesh(mesh);
            }
            EngineStats.Add(EngineStats.ColPrepSel);
            _collisionSelectionRebuildTotal++;
            _collisionGeometrySelectionScene = scene;
            _collisionGeometrySelectionVersion = selectionVersion;
            _collisionGeometryMeshCount = meshCount;
            _collisionFullPassNeeded = true;
        }
        else if (optimized)
        {
            // Incremental: only entities whose own state changed and newly created meshes need a look.
            SelectionChangeBuffer.Clear();
            scene.TakeSelectionChanges(SelectionChangeBuffer);
            foreach (var id in SelectionChangeBuffer)
                if (MeshEntities.TryGetValue(id, out var changed)) ReevaluateSelection(scene, changed);
            SelectionChangeBuffer.Clear();
            if (PendingSelectionMeshes.Count != 0)
            {
                foreach (var id in PendingSelectionMeshes)
                    if (MeshEntities.TryGetValue(id, out var added)) ReevaluateSelection(scene, added);
                PendingSelectionMeshes.Clear();
            }
        }
        // Incremental: only geometries whose own surfaces changed since the last prepare need a look.
        if (optimized && !_collisionFullPassNeeded && PendingCollisionGeometries.Count == 0) return;
        var full = _collisionFullPassNeeded || !optimized;

        if (full)
        {
            foreach (var mesh in PolygonCollisionMeshes) PrepareCollisionMesh(scene, mesh);
        }
        else
        {
            foreach (var geometry in PendingCollisionGeometries)
                foreach (var mesh in geometry.SelectedMeshes) PrepareCollisionMesh(scene, mesh);
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
        foreach (var mesh in PolygonCollisionMeshes)
        {
            mesh.Geometry.InCollisionSet = false;
            mesh.Geometry.SelectedMeshes.Clear();
            mesh.SelectionIndex = -1;
        }
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

    /// <summary>Test oracle: selected set differs from a full scan with the current rules (0 = identical).</summary>
    internal static int CollisionSelectionMismatchForTesting()
    {
        var scene = RequireScene();
        var mismatch = 0;
        foreach (var mesh in MeshEntities.Values)
        {
            var wanted = mesh.Plane is null && scene.RequiresPolygonCollision(mesh.Entity);
            if (wanted != (mesh.SelectionIndex >= 0) || (wanted && !ReferenceEquals(PolygonCollisionMeshes[mesh.SelectionIndex], mesh))) mismatch++;
        }
        return mismatch + Math.Abs(PolygonCollisionMeshes.Count - MeshEntities.Values.Count(m => m.SelectionIndex >= 0));
    }

    private static void ClearCollisionGeometrySelectionCache()
    {
        ResetCollisionSet();
        PendingSelectionMeshes.Clear();
        _collisionGeometrySelectionScene = null;
        _collisionGeometrySelectionVersion = long.MinValue;
        _collisionGeometryMeshCount = -1;
        _collisionFullPassNeeded = true;
    }

    private static SceneVector3 ToSceneVector(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
}
