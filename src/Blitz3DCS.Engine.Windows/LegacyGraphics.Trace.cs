using System.Diagnostics;
using System.Numerics;
using Blitz3DCS.Engine;

namespace Blitz3DCS.Engine.Windows;

// Env-gated diagnostics (BLITZCS_TRACE_ENTITIES, BLITZCS_TRACE_COLLISION, call counts for BLITZCS_ENGINE_PROFILE).
// Every public wrapper takes the plain Core path when EngineTrace.Hooked is false; exceptions are logged by an
// exception filter that returns false, so they always propagate unchanged.
public static partial class LegacyGraphics
{
    private static readonly PeriodicGate SnapshotGate = new(Stopwatch.Frequency);
    private static readonly CollisionSampler PlayerSampler = new();

    public static int LoadMesh(string file, int parent = 0)
    {
        if (!EngineTrace.Hooked) return LoadMeshCore(file, parent);
        var outer = EngineTrace.Enter(TraceCall.LoadMesh);
        try { var id = LoadMeshCore(file, parent); if (outer) EngineTrace.Created("LoadMesh", id, "mesh", file, parent); return id; }
        catch (Exception e) when (EngineTrace.Failed("LoadMesh", e, file)) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static int LoadAnimMesh(string file, int parent = 0)
    {
        if (!EngineTrace.Hooked) return LoadAnimMeshCore(file, parent);
        var outer = EngineTrace.Enter(TraceCall.LoadAnimMesh);
        try { var id = LoadAnimMeshCore(file, parent); if (outer) EngineTrace.Created("LoadAnimMesh", id, "animated", file, parent, animated: true); return id; }
        catch (Exception e) when (EngineTrace.Failed("LoadAnimMesh", e, file)) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static int LoadTexture(string file, int flags = 1)
    {
        if (!EngineTrace.Hooked) return LoadTextureCore(file, flags);
        var outer = EngineTrace.Enter(TraceCall.LoadTexture);
        try { var id = LoadTextureCore(file, flags); if (outer) EngineTrace.Detail("LoadTexture", id, $"src={Path.GetFileName(file)} flags={flags}"); return id; }
        catch (Exception e) when (EngineTrace.Failed("LoadTexture", e, file)) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static int CreateTexture(int width, int height, int flags = 0, int frames = 1)
    {
        if (!EngineTrace.Hooked) return CreateTextureCore(width, height, flags, frames);
        var outer = EngineTrace.Enter(TraceCall.CreateTexture);
        try { var id = CreateTextureCore(width, height, flags, frames); if (outer) EngineTrace.Detail("CreateTexture", id, $"size={width}x{height} frames={frames}"); return id; }
        catch (Exception e) when (EngineTrace.Failed("CreateTexture", e, $"{width}x{height}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static int CopyEntity(int entity, int parent = 0)
    {
        if (!EngineTrace.Hooked) return CopyEntityCore(entity, parent);
        var outer = EngineTrace.Enter(TraceCall.CopyEntity);
        try { var id = CopyEntityCore(entity, parent); if (outer) EngineTrace.Copied(entity, id, parent); return id; }
        catch (Exception e) when (EngineTrace.Failed("CopyEntity", e, $"entity={entity} parent={parent}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static int CreateMesh(int parent = 0)
    {
        if (!EngineTrace.Hooked) return CreateMeshCore(parent);
        var outer = EngineTrace.Enter(TraceCall.MeshBuild);
        try { var id = CreateMeshCore(parent); if (outer) EngineTrace.Created("CreateMesh", id, "mesh", null, parent); return id; }
        catch (Exception e) when (EngineTrace.Failed("CreateMesh", e, $"parent={parent}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static int CreateSprite(int parent = 0)
    {
        if (!EngineTrace.Hooked) return CreateSpriteCore(parent);
        var outer = EngineTrace.Enter(TraceCall.Other);
        try { var id = CreateSpriteCore(parent); if (outer) EngineTrace.Created("CreateSprite", id, "sprite", null, parent); return id; }
        catch (Exception e) when (EngineTrace.Failed("CreateSprite", e, $"parent={parent}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    /// <summary>
    /// Original bbFreeEntity: <c>if( !e ) return; if( debug ){ debugEntity(e); erase(e); } delete e;</c>. Only 0 is a
    /// defined no-op. An unknown or already-freed entity is "Entity does not exist" under the debug flag and an
    /// undefined-behaviour <c>delete</c> of a dangling pointer in a release build, so there is no lenient behaviour to
    /// copy and the adapter keeps reporting it.
    /// </summary>
    public static void FreeEntity(int entity)
    {
        if (!EngineTrace.Hooked) { FreeEntityCore(entity); return; }
        var outer = EngineTrace.Enter(TraceCall.FreeEntity);
        try { FreeEntityCore(entity); if (outer) EngineTrace.Freed(entity); }
        catch (Exception e) when (EngineTrace.Failed("FreeEntity", e, $"entity={entity}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    public static void EntityAlpha(int entity, float alpha)
    {
        if (!EngineTrace.Hooked) { EntityAlphaCore(entity, alpha); return; }
        try { EntityAlphaCore(entity, alpha); EngineTrace.Detail("EntityAlpha", entity, $"alpha={alpha:F2}"); }
        catch (Exception e) when (EngineTrace.Failed("EntityAlpha", e, $"entity={entity}")) { throw; }
    }

    public static void EntityFX(int entity, int effects)
    {
        if (!EngineTrace.Hooked) { EntityFXCore(entity, effects); return; }
        try { EntityFXCore(entity, effects); EngineTrace.Detail("EntityFX", entity, $"fx={effects}"); }
        catch (Exception e) when (EngineTrace.Failed("EntityFX", e, $"entity={entity}")) { throw; }
    }

    private static int _lastRenderCamera;
    private static readonly CollisionTraceSelector PlayerSelector = new();
    private static readonly Dictionary<int, CollisionSampler> EntitySamplers = [];

    /// <summary>Called from Flip when a trace gate is on; emits the once-per-second snapshots.</summary>
    private static void TraceFrame()
    {
        if (!SnapshotGate.Due(Stopwatch.GetTimestamp())) return;
        try
        {
            var scene = RequireScene();
            var fallback = Cameras.Values.FirstOrDefault(state => state.ProjectionMode != 0 && scene.IsVisibleInHierarchy(state.Entity))?.Entity ?? 0;
            var camera = EngineTrace.PickCamera(_lastRenderCamera, Cameras.ContainsKey(_lastRenderCamera), fallback);
            if (EngineTrace.Entities)
            {
                TraceAnimatedEntities(scene, camera);
                if (EngineTrace.TakeStatsLine() is { } stats) EngineTrace.Log(stats);
            }
            if (EngineTrace.Collision) TraceCollisions(scene);
        }
        catch (Exception e) { EngineTrace.Log($"[trace] snapshot failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static void TraceCollisions(SceneGraph scene)
    {
        var candidates = scene.CopyTypedCollisionEntities().Select(c =>
            new CollisionTraceSelector.Candidate(c.Entity, c.Type, c.Collisions,
                new Vector3(scene.EntityX(c.Entity, true), scene.EntityY(c.Entity, true), scene.EntityZ(c.Entity, true))));
        var picks = PlayerSelector.Select(candidates);
        if (picks.Count == 0) { EngineTrace.Log("[collision] no moving or colliding typed entity"); return; }
        foreach (var pick in picks)
        {
            if (!EntitySamplers.TryGetValue(pick.Id, out var sampler)) EntitySamplers[pick.Id] = sampler = new CollisionSampler();
            EngineTrace.Log(sampler.Sample(scene, pick));
        }
    }

    private static void TraceAnimatedEntities(SceneGraph scene, int camera)
    {
        var ids = EngineTrace.AnimatedIds();
        EngineTrace.Log($"[entities] animated={ids.Length} camera={camera}");
        foreach (var id in ids)
        {
            try
            {
                var world = scene.GetWorldTransform(id).Position;
                var pos = new Vector3(world.X, world.Y, world.Z);
                var dist = float.NaN;
                bool? inView = null;
                if (camera != 0 && Cameras.ContainsKey(camera))
                {
                    var cam = scene.GetWorldTransform(camera).Position;
                    dist = EngineTrace.Distance(pos, new Vector3(cam.X, cam.Y, cam.Z));
                    inView = EntityInView(id, camera) != 0;
                }
                var seq = -1; var time = -1f;
                if (AnimatedModels.TryGetValue(id, out var model)) { seq = model.Playback.SequenceIndex; time = model.Playback.SequenceTime; }
                EngineTrace.Log(EngineTrace.FormatAnimatedLine(id, EngineTrace.SourceOf(id), scene.IsSelfVisible(id), scene.IsSelfEnabled(id), pos, dist, inView, seq, time));
            }
            catch (Exception e)
            {
                EngineTrace.Log($"[entities] id={id} gone/unreadable: {e.GetType().Name}");
                EngineTrace.Forget(id);
            }
        }
    }
}
