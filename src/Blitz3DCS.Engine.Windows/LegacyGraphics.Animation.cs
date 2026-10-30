using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    public static void Animate(int entity, int mode = 1, float speed = 1, int sequence = 0, float transition = 0)
    {
        CheckThreadIfStarted();
        var model = RequireAnimation(entity);
        if (mode == 0 && speed == 0)
        {
            model.Playback.Animate(B3dAnimationMode.Stopped, 0);
            return;
        }
        var scene = RequireScene();
        if (transition > 0)
            for (var index = 0; index < model.Entities.Length; index++)
                model.TransitionFrom[index] = scene.GetLocalPose(model.Entities[index]);
        model.Playback.Animate((B3dAnimationMode)mode, speed, sequence, transition);
        model.Playback.EvaluateInto(model.Pose);
        if (model.Playback.IsTransitioning)
            model.Pose.Nodes.CopyTo(model.TransitionDest!);
        else
            ApplyAnimatedPose(model, scene);
    }

    public static void SetAnimTime(int entity, float time, int sequence = 0)
    {
        CheckThreadIfStarted();
        var model = RequireAnimation(entity);
        model.Playback.SetAnimTime(time, sequence);
        model.Playback.EvaluateInto(model.Pose);
        ApplyAnimatedPose(model, RequireScene());
    }

    public static int ExtractAnimSeq(int entity, int firstFrame, int lastFrame, int sequence = 0)
    {
        CheckThreadIfStarted();
        if (!TryAnimation(entity, out var model)) return -1;
        var source = model.Playback.GetSequenceRange(sequence);
        return model.Playback.AddSequenceRange(checked(source.FirstFrame + firstFrame), checked(source.FirstFrame + lastFrame));
    }

    public static int AnimSeq(int entity) => TryAnimation(entity, out var model) ? model.Playback.SequenceIndex : -1;
    public static float AnimTime(int entity) => TryAnimation(entity, out var model) ? model.Playback.SequenceTime : -1;
    public static int AnimLength(int entity) => TryAnimation(entity, out var model) ? model.Playback.SequenceLength : -1;
    public static int Animating(int entity) => TryAnimation(entity, out var model) && model.Playback.IsAnimating ? 1 : 0;

    private static bool TryAnimation(int entity, out AnimatedModel model)
    {
        CheckThreadIfStarted();
        _ = RequireScene().GetLocalPose(entity);
        return AnimatedModels.TryGetValue(entity, out model!) && model.Playback.SequenceCount != 0;
    }

    private static AnimatedModel RequireAnimation(int entity) => TryAnimation(entity, out var model) ? model
        : throw new InvalidOperationException("Entity has no animation.");

    private static void ApplyAnimatedPose(AnimatedModel model, SceneGraph scene)
    {
        var nodes = model.Pose.Nodes;
        for (var index = 0; index < nodes.Length; index++)
            scene.ApplyAnimationPose(model.Entities[index], nodes[index]);
        model.SkinDirty = true;
    }

    private static void UpdateAnimations(SceneGraph scene, float elapsed)
    {
        foreach (var model in AnimatedModels.Values)
        {
            EngineStats.Add(EngineStats.AnimModels);
            if (!scene.IsVisibleInHierarchy(model.Root) || !model.Playback.IsAnimating) continue;
            var poseStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
            EngineStats.Add(EngineStats.AnimActive);
            EngineProfile.Count(EngineProfile.SkinBones, model.Entities.Length);
            model.Playback.Update(elapsed);
            if (!model.Playback.IsTransitioning)
            {
                model.Playback.EvaluateInto(model.Pose);
                ApplyAnimatedPose(model, scene);
                if (EngineProfile.Enabled) EngineProfile.AddSince(EngineProfile.SkinPose, poseStart);
                continue;
            }
            var fraction = model.Playback.TransitionProgress;
            for (var index = 0; index < model.Entities.Length; index++)
            {
                var from = model.TransitionFrom[index];
                var destination = model.TransitionDest![index];
                var rotation = Quaternion.Slerp(new Quaternion(from.RotationX, from.RotationY, from.RotationZ, from.RotationW),
                    new Quaternion(destination.LocalRotation.X, destination.LocalRotation.Y, destination.LocalRotation.Z, destination.LocalRotation.W), fraction);
                scene.ApplyAnimationPose(model.Entities[index], destination with
                {
                    LocalPosition = Interpolate(from.Position, destination.LocalPosition, fraction),
                    LocalScale = Interpolate(from.Scale, destination.LocalScale, fraction),
                    LocalRotation = new B3dQuaternion(rotation.W, rotation.X, rotation.Y, rotation.Z)
                });
            }
            model.SkinDirty = true;
            if (EngineProfile.Enabled) EngineProfile.AddSince(EngineProfile.SkinPose, poseStart);
        }
    }

    private static B3dVector3 Interpolate(SceneVector3 from, B3dVector3 to, float fraction) =>
        new(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction, from.Z + (to.Z - from.Z) * fraction);

    internal static void PrepareAnimatedGeometry()
    {
        var scene = RequireScene();
        foreach (var model in AnimatedModels.Values)
        {
            if (!scene.IsVisibleInHierarchy(model.Root)) { EngineProfile.Count(EngineProfile.SkinSkipped); continue; }
            EngineProfile.Count(EngineProfile.SkinModels);
            var worldStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
            var changed = model.SkinDirty;
            for (var index = 0; index < model.Entities.Length; index++)
            {
                var world = ToWorldMatrix(scene.GetWorldTransform(model.Entities[index]));
                model.CurrentWorld[index] = world;
                changed |= world != model.PreviousWorld[index];
                model.PreviousWorld[index] = world;
            }
            if (EngineProfile.Enabled) EngineProfile.AddSince(EngineProfile.SkinWorld, worldStart);
            foreach (var mesh in model.Meshes)
            {
                if (mesh.Skin is null) continue;
                mesh.Dirty |= changed;
                if (!mesh.Dirty) { EngineProfile.Count(EngineProfile.SkinClean); continue; }
                if (!scene.IsVisibleInHierarchy(mesh.Entity)) { EngineProfile.Count(EngineProfile.SkinSkipped); continue; }
                var deformStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
                EngineProfile.Count(EngineProfile.SkinMeshes);
                EngineProfile.Count(EngineProfile.SkinVerts, mesh.Positions.Length);
                mesh.Skin.DeformInto(model.Pose, model.CurrentWorld, mesh.Positions);
                foreach (var surface in mesh.Surfaces)
                {
                    mesh.Skin.DeformNormalsInto(surface.SourceNormals, surface.DeformedNormals);
                    for (var vertex = 0; vertex < surface.Vertices.Length; vertex++)
                    {
                        var source = surface.BaseSurfaceVertices[vertex];
                        var position = mesh.Positions[vertex];
                        var normal = surface.DeformedNormals[vertex];
                        surface.Vertices[vertex] = new D3D11Vertex(new Vector3(position.X, position.Y, position.Z),
                            source.Color, new Vector3(normal.X, normal.Y, normal.Z), source.TexCoord, source.TexCoord2);
                    }
                    surface.UploadDirty = true;
                }
                mesh.Dirty = false;
                if (EngineProfile.Enabled) EngineProfile.AddSince(EngineProfile.SkinDeform, deformStart);
            }
            model.SkinDirty = false;
        }
    }

    private static D3D11Mesh AnimatedGpuMesh(D3D11Renderer renderer, AnimatedSurface surface)
    {
        if (surface.GpuMesh is null)
            surface.GpuMesh = renderer.CreateDynamicMesh(surface.Vertices, surface.Indices);
        else if (surface.UploadDirty)
        {
            var uploadStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
            renderer.UpdateMeshVertices(surface.GpuMesh, surface.Vertices);
            EngineProfile.Count(EngineProfile.SkinUploads);
            if (EngineProfile.Enabled) EngineProfile.AddSince(EngineProfile.SkinUpload, uploadStart);
        }
        surface.UploadDirty = false;
        return surface.GpuMesh;
    }

    private static void CloseAnimatedGpuMeshes()
    {
        foreach (var model in AnimatedModels.Values)
            foreach (var mesh in model.Meshes)
                foreach (var surface in mesh.Surfaces)
                {
                    surface.GpuMesh?.Dispose();
                    surface.GpuMesh = null;
                    surface.UploadDirty = true;
                }
    }

    private static void ReleaseAnimatedModels(IReadOnlyCollection<int> subtree)
    {
        foreach (var entity in subtree)
        {
            if (!AnimatedModels.Remove(entity, out var model)) continue;
            foreach (var mesh in model.Meshes)
                foreach (var surface in mesh.Surfaces)
                    surface.GpuMesh?.Dispose();
        }
    }

    private static void ValidateAnimationCopy(IReadOnlyCollection<int> sourceIds)
    {
        foreach (var model in AnimatedModels.Values)
        {
            if (sourceIds.Contains(model.Root)) continue;
            foreach (var mesh in model.Meshes)
                if (mesh.Skin is not null && sourceIds.Contains(mesh.Entity))
                    throw new NotSupportedException("Copy the complete animated model hierarchy to preserve its bone bindings.");
        }
    }

    private static void CopyAnimatedModels(int[] sourceIds, int[] copyIds)
    {
        var scene = RequireScene();
        var mapping = new Dictionary<int, int>(sourceIds.Length);
        for (var i = 0; i < sourceIds.Length; i++) mapping.Add(sourceIds[i], copyIds[i]);
        foreach (var sourceId in sourceIds)
        {
            if (!AnimatedModels.TryGetValue(sourceId, out var source)) continue;
            var pose = new B3dPose(source.Model);
            B3dAnimationEvaluator.EvaluateInto(source.Model, 0, pose);
            var entities = new int[source.Entities.Length];
            var locals = new SceneLocalPose[entities.Length];
            var current = new Matrix4x4[entities.Length];
            var previous = new Matrix4x4[entities.Length];
            for (var i = 0; i < entities.Length; i++)
            {
                entities[i] = mapping[source.Entities[i]];
                locals[i] = scene.GetLocalPose(entities[i]);
                current[i] = previous[i] = ToWorldMatrix(scene.GetWorldTransform(entities[i]));
            }
            var meshes = new List<AnimatedMesh>(source.Meshes.Count);
            var copy = new AnimatedModel(source.Model, entities, pose, source.Playback.Copy(), locals, current, previous, meshes)
            { TransitionDest = pose.Nodes.ToArray() };
            foreach (var sourceMesh in source.Meshes)
            {
                var mesh = new AnimatedMesh(mapping[sourceMesh.Entity], sourceMesh.NodeIndex,
                    sourceMesh.Skin is null ? null : new B3dSkinningBuffer(pose, sourceMesh.Skin.MeshNode),
                    new B3dVector3[sourceMesh.Positions.Length], new B3dVector3[sourceMesh.Normals.Length]);
                foreach (var sourceSurface in sourceMesh.Surfaces)
                    mesh.Surfaces.Add(new AnimatedSurface(sourceSurface.Handle, sourceSurface.BaseSurfaceVertices,
                        (D3D11Vertex[])sourceSurface.BaseSurfaceVertices.Clone(), sourceSurface.Indices));
                meshes.Add(mesh);
                GetMesh(mesh.Entity).Animated = mesh;
            }
            AnimatedModels.Add(copy.Root, copy);
        }
    }
}
