namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    /// <summary>
    /// Copies a pivot/mesh hierarchy with independent transforms and shared mesh geometry.
    /// The new root keeps the source's local transform under the requested parent.
    /// </summary>
    private static int CopyEntityCore(int entity, int parent)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        var sourceIds = scene.GetSubtreeEntityIds(entity);
        if (sourceIds.Any(Cameras.ContainsKey))
            throw new NotSupportedException("Copying a camera hierarchy is not implemented by this graphics runtime.");
        ValidateAnimationCopy(sourceIds);

        // MeshModel copies share the whole geometry representation, including
        // its surface list. Validate reference-count growth before modifying the scene.
        var meshGroups = sourceIds.Where(MeshEntities.ContainsKey)
            .Select(id => MeshEntities[id].Geometry).GroupBy(geometry => geometry);
        foreach (var group in meshGroups)
            _ = checked(group.Key.References + group.Count());

        var copy = scene.CopyEntity(entity, parent);
        var copyIds = scene.GetSubtreeEntityIds(copy);
        for (var index = 0; index < sourceIds.Length; index++)
        {
            CopyLightState(sourceIds[index], copyIds[index]);
            if (!MeshEntities.TryGetValue(sourceIds[index], out var mesh)) continue;
            var copiedMesh = new MeshEntity(copyIds[index], mesh.Geometry);
            copiedMesh.Material = mesh.Material.Copy();
            copiedMesh.Order = mesh.Order;
            copiedMesh.AutoFade = mesh.AutoFade;
            if (mesh.Plane is { } plane) copiedMesh.Plane = new PlaneState(plane.Segments);
            MeshEntities.Add(copiedMesh.Entity, copiedMesh);
            NoteMeshAdded(copiedMesh.Entity);
            copiedMesh.Geometry.References++;
            CopySpriteState(sourceIds[index], copyIds[index]);
        }
        CopyAnimatedModels(sourceIds, copyIds);
        return copy;
    }
}
