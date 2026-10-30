using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static readonly List<SceneDraw> SceneDraws = [];
    private static readonly List<int> RenderTraversal = [];
    private static int _trianglesRendered;

    public static void EntityOrder(int entity, int order)
    {
        CheckThreadIfStarted();
        RequireScene().GetWorldTransform(entity);
        if (MeshEntities.TryGetValue(entity, out var mesh)) mesh.Order = order;
    }

    public static void EntityAutoFade(int entity, float near, float far)
    {
        CheckThreadIfStarted();
        GetMesh(entity).AutoFade = (near, far);
    }

    public static int TrisRendered() => _trianglesRendered;

    private static void DrawSceneMeshes(D3D11Renderer renderer, SceneGraph scene, CameraState camera,
        Matrix4x4 cameraWorld, Matrix4x4 view, Matrix4x4 projection)
    {
        SceneDraws.Clear();
        _trianglesRendered = 0;
        var eye = new Vector3(cameraWorld.M41, cameraWorld.M42, cameraWorld.M43);
        scene.CopyVisibleEntityIds(RenderTraversal);
        for (var entityIndex = 0; entityIndex < RenderTraversal.Count; entityIndex++)
        {
            var id = RenderTraversal[entityIndex];
            if (!MeshEntities.TryGetValue(id, out var entity)) continue;
            var entityWorld = ToWorldMatrix(scene.GetWorldTransform(id));
            var distanceSquared = Vector3.DistanceSquared(eye, new Vector3(entityWorld.M41, entityWorld.M42, entityWorld.M43));
            var fade = 1f;
            if (entity.AutoFade is { } range)
            {
                var distance = MathF.Sqrt(distanceSquared);
                if (distance >= range.Far) continue;
                if (distance >= range.Near) fade = 1 - (distance - range.Near) / (range.Far - range.Near);
                if (fade <= 0) continue;
            }
            var isSprite = Sprites.TryGetValue(id, out var sprite);
            var world = isSprite ? BuildSpriteWorldMatrix(sprite!, entityWorld, cameraWorld) : entityWorld;
            var wvp = world * view * projection;
            if (entity.Plane is not null)
            {
                if (!PreparePlane(entity, world, cameraWorld, camera)) continue;
            }
            else if (!isSprite && !IsMeshVisible(entity, wvp)) continue;
            for (var index = 0; index < entity.SurfaceHandles.Count; index++)
            {
                var surface = Surfaces[entity.SurfaceHandles[index]];
                if (surface.Indices.Count == 0 && entity.Plane is null) continue;
                var material = entity.Material;
                ValidateComposedMaterial(surface.Material, material);
                var stages = 0;
                var transparent = false;
                for (var stage = 0; stage < RenderLayers.Length; stage++)
                {
                    var texture = material.Textures[stage] ?? surface.Material.Textures[stage];
                    if (texture is null) continue;
                    stages = stage + 1;
                    transparent |= texture.UseAlpha && !texture.Masked;
                }
                var color = ComposeColor(surface.Material, material);
                color.W *= fade;
                var blend = ResolveMaterialBlend(ComposeBlend(surface.Material, material), stages, transparent,
                    color.W, surface.Material.Effects | material.Effects);
                SceneDraws.Add(new SceneDraw(entity, surface, index, world, wvp, color, blend,
                    entity.Order, distanceSquared, entityIndex));
            }
        }
        SceneDraws.Sort(SceneDrawComparer.Instance);
        foreach (var draw in SceneDraws)
        {
            var material = draw.Entity.Material;
            var surfaceMaterial = draw.Surface.Material;
            for (var index = 0; index < RenderLayers.Length; index++)
            {
                surfaceMaterial.UpdateLayer(index);
                material.UpdateLayer(index);
                RenderLayers[index] = material.Textures[index] is not null ? material.Layers[index] : surfaceMaterial.Layers[index];
            }
            D3D11Mesh gpuMesh;
            if (draw.Entity.Plane is { } plane)
                gpuMesh = PlaneGpuMesh(renderer, plane);
            else if (draw.Entity.Animated is { Skin: not null } animated)
                gpuMesh = AnimatedGpuMesh(renderer, animated.Surfaces[draw.SurfaceIndex]);
            else
            {
                EnsureGpuMesh(renderer, draw.Surface);
                gpuMesh = draw.Surface.GpuMesh!;
            }
            var effects = surfaceMaterial.Effects | material.Effects;
            var fog = (effects & 8) != 0 ? null : GetCameraFogForRendering(camera.Entity);
            var depthMode = draw.Order != 0 ? 2 : draw.Blend != 0 ? 1 : 0;
            var state = new D3D11LightingDrawState(effects, surfaceMaterial.Shininess + material.Shininess, draw.Blend, depthMode, _wireframe);
            renderer.DrawMesh(gpuMesh, draw.WorldViewProjection, RenderLayers, draw.Blend == 1, draw.Color, fog,
                draw.World * view, state, draw.World, draw.Entity.Plane is { } activePlane ? (uint)activePlane.Grid.IndexCount : null);
            _trianglesRendered += draw.Entity.Plane is { } renderedPlane ? renderedPlane.Grid.IndexCount / 3 : draw.Surface.Indices.Count / 3;
        }
    }

    private readonly record struct SceneDraw(MeshEntity Entity, SurfaceState Surface, int SurfaceIndex,
        Matrix4x4 World, Matrix4x4 WorldViewProjection, Vector4 Color, int Blend, int Order,
        float DistanceSquared, int EntityIndex)
    {
        internal int Category => Order > 0 ? 0 : Order < 0 ? 3 : Blend == 0 ? 1 : 2;
    }

    private sealed class SceneDrawComparer : IComparer<SceneDraw>
    {
        internal static readonly SceneDrawComparer Instance = new();
        public int Compare(SceneDraw left, SceneDraw right)
        {
            var comparison = left.Category.CompareTo(right.Category);
            if (comparison != 0) return comparison;
            if (left.Category is 0 or 3)
            {
                comparison = right.Order.CompareTo(left.Order);
                if (comparison != 0) return comparison;
            }
            if (left.Category == 2)
            {
                comparison = right.DistanceSquared.CompareTo(left.DistanceSquared);
                if (comparison != 0) return comparison;
            }
            comparison = left.EntityIndex.CompareTo(right.EntityIndex);
            if (comparison != 0) return comparison;
            comparison = (left.Blend != 0).CompareTo(right.Blend != 0);
            return comparison != 0 ? comparison : left.SurfaceIndex.CompareTo(right.SurfaceIndex);
        }
    }
}
