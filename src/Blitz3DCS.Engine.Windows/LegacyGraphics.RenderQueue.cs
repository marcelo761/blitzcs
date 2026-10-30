using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static SceneDraw[] _sceneDraws = new SceneDraw[256];
    private static int _sceneDrawCount;
    private static int[] _drawOrder = new int[256];
    private static readonly SceneDrawIndexComparer IndexComparer = new();
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

    private static void ClearSceneDraws()
    {
        Array.Clear(_sceneDraws, 0, _sceneDrawCount);
        _sceneDrawCount = 0;
    }

    public static int TrisRendered() => _trianglesRendered;

    private static void DrawSceneMeshes(D3D11Renderer renderer, SceneGraph scene, CameraState camera,
        Matrix4x4 cameraWorld, Matrix4x4 view, Matrix4x4 projection)
    {
        var traversalStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        _sceneDrawCount = 0;
        _trianglesRendered = 0;
        var eye = new Vector3(cameraWorld.M41, cameraWorld.M42, cameraWorld.M43);
        scene.CopyVisibleEntityIds(RenderTraversal);
        EngineProfile.Count(EngineProfile.RtVisited, RenderTraversal.Count);
        for (var entityIndex = 0; entityIndex < RenderTraversal.Count; entityIndex++)
        {
            var id = RenderTraversal[entityIndex];
            if (!MeshEntities.TryGetValue(id, out var entity)) continue;
            EngineProfile.Count(EngineProfile.RtMeshes);
            var entityWorld = ToWorldMatrix(scene.GetWorldTransform(id));
            var distanceSquared = Vector3.DistanceSquared(eye, new Vector3(entityWorld.M41, entityWorld.M42, entityWorld.M43));
            var fade = 1f;
            if (entity.AutoFade is { } range)
            {
                var distance = MathF.Sqrt(distanceSquared);
                if (distance >= range.Far) { EngineProfile.Count(EngineProfile.RtCullDist); continue; }
                if (distance >= range.Near) fade = 1 - (distance - range.Near) / (range.Far - range.Near);
                if (fade <= 0) { EngineProfile.Count(EngineProfile.RtCullDist); continue; }
            }
            var isSprite = Sprites.TryGetValue(id, out var sprite);
            var world = isSprite ? BuildSpriteWorldMatrix(sprite!, entityWorld, cameraWorld) : entityWorld;
            var wvp = world * view * projection;
            if (entity.Plane is not null)
            {
                if (!PreparePlane(entity, world, cameraWorld, camera)) { EngineProfile.Count(EngineProfile.RtCullFrustum); continue; }
            }
            else if (!isSprite && !IsMeshVisible(entity, wvp)) { EngineProfile.Count(EngineProfile.RtCullFrustum); continue; }
            for (var index = 0; index < entity.SurfaceHandles.Count; index++)
            {
                var surface = Surfaces[entity.SurfaceHandles[index]];
                if (surface.Indices.Count == 0 && entity.Plane is null) continue;
                var material = entity.Material;
                ValidateComposedMaterial(surface.Material, material);
                var stages = 0;
                var transparent = false;
                var masked = false;
                for (var stage = 0; stage < RenderLayers.Length; stage++)
                {
                    var texture = material.Textures[stage] ?? surface.Material.Textures[stage];
                    if (texture is null) continue;
                    if (EngineProfile.Enabled) masked |= texture.Masked;
                    stages = stage + 1;
                    transparent |= texture.UseAlpha && !texture.Masked;
                }
                var color = ComposeColor(surface.Material, material);
                color.W *= fade;
                var blend = ResolveMaterialBlend(ComposeBlend(surface.Material, material), stages, transparent,
                    color.W, surface.Material.Effects | material.Effects);
                if (EngineProfile.Enabled) CountDrawCategory(blend, isSprite, masked, entity.Animated is { Skin: not null });
                if (_sceneDrawCount == _sceneDraws.Length)
                    Array.Resize(ref _sceneDraws, _sceneDraws.Length * 2);
                _sceneDraws[_sceneDrawCount++] = new SceneDraw(entity, surface, index, world, wvp, color, blend,
                    entity.Order, distanceSquared, entityIndex);
            }
        }
        var drawCount = _sceneDrawCount;
        var draws = _sceneDraws;
        if (_drawOrder.Length < drawCount) _drawOrder = new int[draws.Length];
        var order = _drawOrder;
        var sortStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        if (EngineProfile.Enabled) EngineProfile.AddTicks(EngineProfile.RtTraverse, sortStart - traversalStart);
        EngineProfile.Count(EngineProfile.RtSurfaces, drawCount);
        EngineProfile.Count(EngineProfile.RtSortKeys, drawCount);
        for (var i = 0; i < drawCount; i++) order[i] = i;
        // The comparer is a strict total order (entity index + surface index are unique per draw),
        // so an unstable sort over an index array yields the same sequence as sorting the structs.
        if (EngineOptimizations.Disabled)
            Array.Sort(draws, 0, drawCount, SceneDrawComparer.Instance);
        else
        {
            IndexComparer.Draws = draws;
            Array.Sort(order, 0, drawCount, IndexComparer);
            IndexComparer.Draws = null;
        }
        if (EngineProfile.Enabled) EngineProfile.AddSince(EngineProfile.RtSort, sortStart);
        D3D11Fog? cameraFog = null;
        if (drawCount > 0) cameraFog = GetCameraFogForRendering(camera.Entity);
        var legacyOrder = EngineOptimizations.Disabled;
        var submitStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        if (EngineProfile.Enabled) EngineProfile.AddTicks(EngineProfile.Traversal, submitStart - traversalStart);
        for (var drawIndex = 0; drawIndex < drawCount; drawIndex++)
        {
            ref readonly var draw = ref draws[legacyOrder ? drawIndex : order[drawIndex]];
            var stateStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
            var material = draw.Entity.Material;
            var surfaceMaterial = draw.Surface.Material;
            for (var index = 0; index < RenderLayers.Length; index++)
            {
                surfaceMaterial.UpdateLayer(index);
                material.UpdateLayer(index);
                RenderLayers[index] = material.Textures[index] is not null ? material.Layers[index] : surfaceMaterial.Layers[index];
            }
            if (EngineProfile.Enabled) CountDrawState(in draw, surfaceMaterial.Effects | material.Effects, stateStart);
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
            var fog = (effects & 8) != 0 ? null : cameraFog;
            var depthMode = draw.Order != 0 ? 2 : draw.Blend != 0 ? 1 : 0;
            var state = new D3D11LightingDrawState(effects, surfaceMaterial.Shininess + material.Shininess, draw.Blend, depthMode, _wireframe);
            renderer.DrawMesh(gpuMesh, draw.WorldViewProjection, RenderLayers, draw.Blend == 1, draw.Color, fog,
                draw.World * view, state, draw.World, draw.Entity.Plane is { } activePlane ? (uint)activePlane.Grid.IndexCount : null);
            _trianglesRendered += draw.Entity.Plane is { } renderedPlane ? renderedPlane.Grid.IndexCount / 3 : draw.Surface.Indices.Count / 3;
        }
        if (EngineProfile.Enabled)
        {
            EngineProfile.Count(EngineProfile.RtUniqueMat, StatMaterialKeys.Count);
            StatMaterialKeys.Clear();
            _statHasPrevious = false;
            EngineProfile.Add(EngineProfile.Submit, submitStart);
        }
    }

    // Profiling-only state (touched only when BLITZCS_ENGINE_PROFILE=1).
    private static readonly HashSet<int> StatMaterialKeys = [];
    private static readonly D3D11TextureLayer[] StatPreviousLayers = new D3D11TextureLayer[8];
    private static int _statPreviousBlend, _statPreviousEffects, _statPreviousOrder;
    private static bool _statHasPrevious;

    private static void CountDrawCategory(int blend, bool sprite, bool masked, bool animated)
    {
        EngineProfile.Count(blend switch { 0 => EngineProfile.RtOpaque, 1 => EngineProfile.RtAlpha, 3 => EngineProfile.RtAdditive, _ => EngineProfile.RtOtherBlend });
        if (sprite) EngineProfile.Count(EngineProfile.RtSprite);
        if (masked) EngineProfile.Count(EngineProfile.RtMasked);
        if (animated) EngineProfile.Count(EngineProfile.RtAnim);
    }

    /// <summary>Counts unique material/texture sets and draws whose state equals the previous draw's; times state building.</summary>
    private static void CountDrawState(in SceneDraw draw, int effects, long stateStart)
    {
        var hash = new HashCode();
        hash.Add(draw.Blend); hash.Add(effects); hash.Add(draw.Order != 0);
        var same = _statHasPrevious && _statPreviousBlend == draw.Blend && _statPreviousEffects == effects
            && _statPreviousOrder == draw.Order && ((ReadOnlySpan<D3D11TextureLayer>)RenderLayers).SequenceEqual(StatPreviousLayers);
        for (var i = 0; i < RenderLayers.Length; i++) hash.Add(RenderLayers[i]);
        StatMaterialKeys.Add(hash.ToHashCode());
        if (same) EngineProfile.Count(EngineProfile.RtBatchable);
        RenderLayers.CopyTo(StatPreviousLayers, 0);
        _statPreviousBlend = draw.Blend; _statPreviousEffects = effects; _statPreviousOrder = draw.Order; _statHasPrevious = true;
        EngineProfile.AddSince(EngineProfile.RtState, stateStart);
    }

    private readonly record struct SceneDraw(MeshEntity Entity, SurfaceState Surface, int SurfaceIndex,
        Matrix4x4 World, Matrix4x4 WorldViewProjection, Vector4 Color, int Blend, int Order,
        float DistanceSquared, int EntityIndex)
    {
        internal int Category => Order > 0 ? 0 : Order < 0 ? 3 : Blend == 0 ? 1 : 2;
    }

    private sealed class SceneDrawIndexComparer : IComparer<int>
    {
        internal SceneDraw[]? Draws;
        public int Compare(int left, int right) => SceneDrawComparer.Instance.CompareRef(in Draws![left], in Draws[right]);
    }

    private sealed class SceneDrawComparer : IComparer<SceneDraw>
    {
        internal static readonly SceneDrawComparer Instance = new();
        public int Compare(SceneDraw left, SceneDraw right) => CompareRef(in left, in right);

        internal int CompareRef(in SceneDraw left, in SceneDraw right)
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
