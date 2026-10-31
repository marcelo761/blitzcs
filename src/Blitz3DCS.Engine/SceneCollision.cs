using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Engine;

/// <summary>A collision triangle in entity-local coordinates.</summary>
public readonly record struct SceneCollisionTriangle(SceneVector3 A, SceneVector3 B, SceneVector3 C, int Index = -1, int Surface = 0);

internal readonly record struct SceneCollisionInfo(
    int Entity,
    SceneVector3 Position,
    SceneVector3 Normal,
    float Time,
    int Triangle,
    int Surface);

/// <summary>Collision configuration and swept collision queries for a scene.</summary>
public sealed partial class SceneGraph
{
    private const int CollisionMethodSphere = 1;
    private const int CollisionMethodPolygon = 2;
    private const int CollisionMethodBox = 3;
    private const int CollisionResponseNone = 0;
    private const int CollisionResponseStop = 1;
    private const int CollisionResponseSlide = 2;
    private const int CollisionResponseSlideXZ = 3;
    private const int MaxCollisionType = 1000;
    private const int MaxCollisionHits = 10;
    private const float CollisionEpsilon = 0.001f;
    private const float Epsilon = 0.000001f;

    private readonly Dictionary<int, CollisionEntityState> _collisionEntities = [];
    private readonly List<CollisionRule> _collisionRules = [];
    private readonly List<Node> _enabledCollisionNodes = [];
    private readonly List<CollisionEntityState> _enabledCollisionStates = [];
    private readonly List<Node>?[] _collisionEntitiesByType = new List<Node>?[MaxCollisionType];
    private readonly CollisionPlane[] _collisionPlanes = new CollisionPlane[2];
    internal bool ForceLinearPolygonSearchForTesting { get; set; }

    // Persistent collision lists, rebuilt only when _structureVersion changes (create/free/reparent/show/hide/type/rules).
    private int _collisionCacheVersion = -1;
    private int _enabledCount;
    private Node[] _typedNodes = [];
    private CollisionEntityState[] _typedStates = [];
    private int _typedCount;
    private int[] _sourceIndices = [];
    private int _sourceCount;
    private readonly CollisionRule[]?[] _rulesBySource = new CollisionRule[]?[MaxCollisionType];
    private bool _legacyCollisionPath;

    /// <summary>Testing hook: runs this graph on the original per-frame collection path (same as BLITZCS_ENGINE_NOOPT=1).</summary>
    internal bool UseLegacyCollisionPathForTesting { get => _legacyCollisionPath; set => _legacyCollisionPath = value; }
    private long _polygonGeometrySelectionVersion;

    /// <summary>Changes when entity membership in polygon collision geometry can change.</summary>
    public long PolygonGeometrySelectionVersion => _polygonGeometrySelectionVersion;

    // Entities whose polygon-collision requirement may have changed since the last TakeSelectionChanges. Rule changes
    // (which affect every entity) bump the version instead; so does overflowing this set.
    private readonly HashSet<int> _selectionDirty = [];
    private const int SelectionDirtyLimit = 4096;

    /// <summary>Marks one entity's polygon-collision requirement as possibly changed (original behaviour: bump the global version).</summary>
    private void NoteSelectionChanged(int entity)
    {
        if (!EngineOptimizations.IncrSelection) { _polygonGeometrySelectionVersion++; return; }
        if (_selectionDirty.Count >= SelectionDirtyLimit) { _selectionDirty.Clear(); _polygonGeometrySelectionVersion++; return; }
        _selectionDirty.Add(entity);
    }

    /// <summary>Moves the entities whose polygon-collision requirement may have changed into <paramref name="entities"/> and forgets them.</summary>
    public void TakeSelectionChanges(List<int> entities)
    {
        entities.AddRange(_selectionDirty);
        _selectionDirty.Clear();
    }

    /// <summary>Sets an entity's collision type. A nonzero recursive flag applies it to descendants.</summary>
    public void EntityType(int entity, int type, bool recursive = false)
    {
        if (type is < 0 or >= MaxCollisionType) throw new ArgumentOutOfRangeException(nameof(type));
        var node = GetNode(entity);
        IEnumerable<Node> targets = recursive ? GetSubtreeNodes(node) : [node];
        var changed = false;
        foreach (var target in targets)
        {
            var state = GetCollisionState(target.Id);
            EngineStats.Add(EngineStats.SelBumpType);
            if (state.Type != type || !EngineOptimizations.IncrSelection) { changed = true; NoteSelectionChanged(target.Id); }
            state.Type = type;
            ResetCollisionState(state, target);
        }
        // An unchanged type cannot alter the collision lists or the polygon selection.
        if (changed || !EngineOptimizations.IncrSelection) _structureVersion++;
    }

    public int GetEntityType(int entity) => GetCollisionState(entity).Type;

    /// <summary>Whether configured rules can test this entity's polygon geometry.</summary>
    public bool RequiresPolygonCollision(int entity)
    {
        return RequiresPolygonCollisionOrPicking(entity);
    }

    /// <summary>Sets source collision radii; a zero Y radius uses the X radius.</summary>
    public void EntityRadius(int entity, float xRadius, float yRadius = 0)
    {
        var state = GetCollisionState(entity);
        state.Radii = new(xRadius, yRadius == 0 ? xRadius : yRadius, xRadius);
    }

    /// <summary>Sets the local axis-aligned box used when this entity is a box collision target.</summary>
    public void EntityBox(int entity, float x, float y, float z, float width, float height, float depth)
    {
        var state = GetCollisionState(entity);
        state.BoxMinimum = new(MathF.Min(x, x + width), MathF.Min(y, y + height), MathF.Min(z, z + depth));
        state.BoxMaximum = new(MathF.Max(x, x + width), MathF.Max(y, y + height), MathF.Max(z, z + depth));
    }

    /// <summary>Replaces local collision triangles for polygon collision mode.</summary>
    public void SetCollisionTriangles(int entity, IReadOnlyList<SceneCollisionTriangle> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        var state = GetCollisionState(entity);
        state.Triangles = triangles.ToArray();
        state.TriangleCollider = null;
        state.BoundValid = false;
        state.TriangleCandidates.Clear();
    }

    /// <summary>Uses an entity's local y=0 plane as infinite polygon collision geometry.</summary>
    public void SetInfinitePlane(int entity, bool enabled = true)
    {
        var state = GetCollisionState(entity);
        EngineStats.Add(EngineStats.SelBumpPlane);
        if (state.InfinitePlane != enabled || !EngineOptimizations.IncrSelection) NoteSelectionChanged(entity);
        state.InfinitePlane = enabled;
    }

    internal bool HasTriangleColliderForTesting(int entity) => GetCollisionState(entity).TriangleCollider is not null;

    /// <summary>Registers a directed type-pair collision rule; the first destination rule wins.</summary>
    public void Collisions(int sourceType, int destinationType, int method, int response)
    {
        ValidateCollisionType(sourceType, nameof(sourceType));
        ValidateCollisionType(destinationType, nameof(destinationType));
        if (method is < CollisionMethodSphere or > CollisionMethodBox) throw new ArgumentOutOfRangeException(nameof(method));
        if (response is < CollisionResponseNone or > CollisionResponseSlideXZ) throw new ArgumentOutOfRangeException(nameof(response));
        if (_collisionRules.Any(rule => rule.SourceType == sourceType && rule.DestinationType == destinationType)) return;
        _collisionRules.Add(new(sourceType, destinationType, method, response));
        EngineStats.Add(EngineStats.SelBumpRules);
        _structureVersion++;
        _polygonGeometrySelectionVersion++;
    }

    /// <summary>Clears configured type-pair rules. Existing collision results remain until the next update.</summary>
    public void ClearCollisions()
    {
        EngineStats.Add(EngineStats.SelBumpRules);
        if (_collisionRules.Count == 0 && EngineOptimizations.IncrSelection) return;
        _collisionRules.Clear();
        _structureVersion++;
        _polygonGeometrySelectionVersion++;
    }

    /// <summary>Updates collision results and responses using movement since the previous update.</summary>
    public void UpdateWorld(float elapsed = 1)
    {
        _ = elapsed;
        var statStart = EngineStats.Now();
        if (!EngineOptimizations.CollState || _legacyCollisionPath) { UpdateWorldOriginal(statStart); return; }

        if (_collisionCacheVersion != _structureVersion) RebuildCollisionCache();
        if (EngineStats.Enabled)
        {
            EngineStats.Add(EngineStats.SceneNodes, _nodes.Count);
            EngineStats.Add(EngineStats.SceneVisited, _enabledCount);
        }

        var nodes = _typedNodes;
        var states = _typedStates;
        var typedCount = _typedCount;
        // Untyped entities never hold collision results (EntityType resets them), so only typed ones need clearing.
        for (var i = 0; i < typedCount; i++)
        {
            var collisions = states[i].Collisions;
            if (collisions.Count != 0) collisions.Clear();
        }

        EngineStats.AddTicks(EngineStats.ColBroad, statStart);
        var sources = _sourceIndices;
        for (var i = 0; i < _sourceCount; i++)
        {
            var index = sources[i];
            ProcessCollisionsTimed(nodes[index], states[index]);
        }

        // Previous transforms of untyped entities are unobservable: EntityType/Show/Hide/Copy reset them on use.
        for (var i = 0; i < typedCount; i++)
        {
            var state = states[i];
            var current = GetWorldTransform(nodes[i]);
            if (EngineStats.Enabled)
            {
                EngineStats.Add(EngineStats.ColEnabled);
                if (state.HasPreviousTransform && state.PreviousTransform == current) EngineStats.Add(EngineStats.ColStill);
            }
            state.PreviousTransform = current;
            state.HasPreviousTransform = true;
        }
    }

    private void RebuildCollisionCache()
    {
        _collisionCacheVersion = _structureVersion;
        foreach (var typed in _collisionEntitiesByType) typed?.Clear();
        Array.Clear(_rulesBySource);
        foreach (var rule in _collisionRules)
        {
            var existing = _rulesBySource[rule.SourceType];
            if (existing is null) { _rulesBySource[rule.SourceType] = [rule]; continue; }
            Array.Resize(ref existing, existing.Length + 1);
            existing[^1] = rule;
            _rulesBySource[rule.SourceType] = existing;
        }

        _enabledCount = 0;
        _typedCount = 0;
        var roots = CollectionsMarshal.AsSpan(_roots);
        for (var i = 0; i < roots.Length; i++) GatherEnabled(roots[i]);

        if (_sourceIndices.Length < _typedCount) _sourceIndices = new int[_typedNodes.Length];
        _sourceCount = 0;
        for (var i = 0; i < _typedCount; i++)
            if (_rulesBySource[_typedStates[i].Type] is not null) _sourceIndices[_sourceCount++] = i;
    }

    private void GatherEnabled(Node node)
    {
        if (!node.Enabled) return;
        _enabledCount++;
        var state = GetCollisionState(node);
        if (state.Type != 0)
        {
            if (_typedCount == _typedNodes.Length)
            {
                var capacity = Math.Max(16, _typedCount * 2);
                Array.Resize(ref _typedNodes, capacity);
                Array.Resize(ref _typedStates, capacity);
            }
            _typedNodes[_typedCount] = node;
            _typedStates[_typedCount] = state;
            _typedCount++;
            (_collisionEntitiesByType[state.Type] ??= []).Add(node);
        }
        var children = CollectionsMarshal.AsSpan(node.Children);
        for (var i = 0; i < children.Length; i++) GatherEnabled(children[i]);
    }

    /// <summary>The original per-frame path: rescans every node for roots and re-collects all enabled entities.</summary>
    private void UpdateWorldOriginal(long statStart)
    {
        _enabledCollisionNodes.Clear();
        foreach (var typed in _collisionEntitiesByType) typed?.Clear();
        foreach (var node in _nodes.Values)
            if (node.Parent is null) CollectEnabled(node, _enabledCollisionNodes);

        if (EngineStats.Enabled)
        {
            EngineStats.Add(EngineStats.SceneNodes, _nodes.Count);
            EngineStats.Add(EngineStats.SceneVisited, _enabledCollisionNodes.Count);
        }
        // Cached lists are not maintained on this path; force a rebuild if a graph ever switches back.
        _collisionCacheVersion = -1;
        UpdateWorldLegacy();
        EngineStats.AddTicks(EngineStats.ColBroad, statStart);
    }

    private long _statFind, _statTry, _statQuery;

    /// <summary>Stats wrapper: splits one source's cost into selection (broad), narrow-phase and response ticks.</summary>
    private void ProcessCollisionsTimed(Node source, CollisionEntityState sourceState)
    {
        if (!EngineStats.Enabled) { ProcessCollisions(source, sourceState); return; }
        _statFind = _statTry = _statQuery = 0;
        EngineStats.Add(EngineStats.ColSources);
        var start = Stopwatch.GetTimestamp();
        ProcessCollisions(source, sourceState);
        var total = Stopwatch.GetTimestamp() - start;
        EngineStats.AddRawTicks(EngineStats.ColBroad, _statFind - _statTry + _statQuery);
        EngineStats.AddRawTicks(EngineStats.ColNarrow, _statTry - _statQuery);
        EngineStats.AddRawTicks(EngineStats.ColResp, total - _statFind);
    }

    private void UpdateWorldLegacy()
    {
        foreach (var node in _enabledCollisionNodes)
        {
            var state = GetCollisionState(node.Id);
            state.Collisions.Clear();
            if (state.Type == 0) continue;
            (_collisionEntitiesByType[state.Type] ??= []).Add(node);
        }

        foreach (var source in _enabledCollisionNodes)
        {
            var state = GetCollisionState(source.Id);
            if (state.Type != 0 && HasSourceCollisionRule(state.Type))
                ProcessCollisionsTimed(source, state);
        }

        foreach (var node in _enabledCollisionNodes)
        {
            var state = GetCollisionState(node.Id);
            state.PreviousTransform = GetWorldTransform(node);
            state.HasPreviousTransform = true;
        }
    }

    /// <summary>Diagnostics: total entity count and the number of entities per nonzero collision type id.</summary>
    internal int CountEntitiesAndCollisionTypes(SortedDictionary<int, int> typeCounts)
    {
        foreach (var state in _collisionEntities.Values)
            if (state.Type != 0) typeCounts[state.Type] = typeCounts.GetValueOrDefault(state.Type) + 1;
        return _nodes.Count;
    }

    public int CountCollisions(int entity) => GetCollisionState(entity).Collisions.Count;

    /// <summary>Diagnostics: (entity, collision type, current collision count) for every entity with a nonzero type.</summary>
    public List<(int Entity, int Type, int Collisions)> CopyTypedCollisionEntities()
    {
        var result = new List<(int, int, int)>();
        foreach (var pair in _collisionEntities)
            if (pair.Value.Type != 0) result.Add((pair.Key, pair.Value.Type, pair.Value.Collisions.Count));
        return result;
    }

    public int EntityCollided(int entity, int collisionType)
    {
        var collisions = GetCollisionState(entity).Collisions;
        foreach (var collision in collisions)
            if (GetCollisionState(collision.Entity).Type == collisionType) return collision.Entity;
        return 0;
    }

    public int CollisionEntity(int entity, int index) => GetCollision(entity, index).Entity;
    public float CollisionX(int entity, int index) => GetCollision(entity, index).Position.X;
    public float CollisionY(int entity, int index) => GetCollision(entity, index).Position.Y;
    public float CollisionZ(int entity, int index) => GetCollision(entity, index).Position.Z;
    public float CollisionNX(int entity, int index) => GetCollision(entity, index).Normal.X;
    public float CollisionNY(int entity, int index) => GetCollision(entity, index).Normal.Y;
    public float CollisionNZ(int entity, int index) => GetCollision(entity, index).Normal.Z;
    public float CollisionTime(int entity, int index) => GetCollision(entity, index).Time;
    public int CollisionTriangle(int entity, int index) => GetCollision(entity, index).Triangle;
    public int CollisionSurface(int entity, int index) => GetCollision(entity, index).Surface;

    /// <summary>Clears collision results and sets the motion sweep origin to the entity's current world transform.</summary>
    public void ResetEntity(int entity) => ResetCollisionState(GetCollisionState(entity), GetNode(entity));

    internal void ForgetCollisionState(int entity)
    {
        EngineStats.Add(EngineStats.SelBumpForget);
        var existed = _collisionEntities.Remove(entity);
        if (_nodes.TryGetValue(entity, out var forgotten)) forgotten.CollisionState = null;
        if (_pickedEntity == entity) _pickedEntity = 0;
        if (!existed && EngineOptimizations.IncrSelection) return; // never had collision state: nothing references it
        _structureVersion++;
        NoteSelectionChanged(entity);
        foreach (var state in _collisionEntities.Values)
            state.Collisions.RemoveAll(collision => collision.Entity == entity);
    }

    internal void CopyCollisionState(int source, int destination)
    {
        EngineStats.Add(EngineStats.SelBumpCopyState);
        if (!_collisionEntities.TryGetValue(source, out var sourceState))
        {
            // A source without state is the default state; the copy stays stateless.
            if (!EngineOptimizations.IncrSelection) { _structureVersion++; _polygonGeometrySelectionVersion++; }
            return;
        }
        _structureVersion++;
        NoteSelectionChanged(destination);
        var target = GetNode(destination);
        var state = new CollisionEntityState
        {
            Type = sourceState.Type,
            Radii = sourceState.Radii,
            BoxMinimum = sourceState.BoxMinimum,
            BoxMaximum = sourceState.BoxMaximum,
            PickMode = sourceState.PickMode,
            PickObscurer = sourceState.PickObscurer,
            InfinitePlane = sourceState.InfinitePlane,
            Triangles = sourceState.Triangles.ToArray(),
            PreviousTransform = GetWorldTransform(target),
            HasPreviousTransform = true
        };
        _collisionEntities[destination] = state;
        target.CollisionState = state;
    }

    internal void ResetCollisionState(int entity)
    {
        if (_collisionEntities.TryGetValue(entity, out var state)) ResetCollisionState(state, GetNode(entity));
    }

    private void ResetCollisionState(CollisionEntityState state, Node node)
    {
        state.Collisions.Clear();
        state.PreviousTransform = GetWorldTransform(node);
        state.HasPreviousTransform = true;
    }

    private CollisionEntityState GetCollisionState(int entity) => GetCollisionState(GetNode(entity));

    private CollisionEntityState GetCollisionState(Node node)
    {
        var cached = node.CollisionState;
        if (cached is not null && EngineOptimizations.CollState) return cached;
        if (_collisionEntities.TryGetValue(node.Id, out var state)) { node.CollisionState = state; return state; }
        state = new CollisionEntityState { PreviousTransform = GetWorldTransform(node), HasPreviousTransform = true };
        _collisionEntities.Add(node.Id, state);
        node.CollisionState = state;
        return state;
    }

    private SceneCollisionInfo GetCollision(int entity, int index)
    {
        var collisions = GetCollisionState(entity).Collisions;
        if (index < 1 || index > collisions.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return collisions[index - 1];
    }

    private static void ValidateCollisionType(int type, string parameterName)
    {
        if (type is < 0 or >= MaxCollisionType) throw new ArgumentOutOfRangeException(parameterName);
    }

    private void CollectEnabled(Node node, List<Node> result)
    {
        if (!node.Enabled) return;
        result.Add(node);
        foreach (var child in node.Children) CollectEnabled(child, result);
    }

    private bool HasSourceCollisionRule(int sourceType)
    {
        foreach (var rule in _collisionRules)
            if (rule.SourceType == sourceType) return true;
        return false;
    }

    private void ProcessCollisions(Node source, CollisionEntityState sourceState)
    {
        var destination = GetWorldTransform(source).Position;
        var origin = sourceState.PreviousTransform.Position;
        if (NearlyEqual(origin, destination))
        {
            if (origin != destination) SetWorldPosition(source, origin);
            return;
        }

        var panicPosition = origin;
        var yScale = sourceState.Radii.X == sourceState.Radii.Y ? 1 : sourceState.Radii.X / sourceState.Radii.Y;
        var inverseYScale = 1 / yScale;
        origin = ScaleY(origin, yScale);
        destination = ScaleY(destination, yScale);
        var lineStart = origin;
        var lineDirection = Subtract(destination, origin);
        var originalDirection = lineDirection;
        var remainingDistance = Length(lineDirection);
        var remainingXZDistance = Length(new(lineDirection.X, 0, lineDirection.Z));
        var sourceRadius = sourceState.Radii.X;
        var planeCount = 0;
        var planes = _collisionPlanes;
        var hitCount = 0;

        for (var iteration = 0; iteration < MaxCollisionHits; iteration++)
        {
            EngineStats.Add(EngineStats.ColRespIters);
            if (!FindNearestHit(source.Id, sourceState.Type, lineStart, lineDirection, sourceRadius, yScale, out var hit)) break;
            if (++hitCount == MaxCollisionHits) break;
            var contact = Add(lineStart, Multiply(lineDirection, hit.Time));
            var resultPosition = ScaleY(Subtract(contact, Multiply(hit.Normal, sourceRadius)), inverseYScale);
            EngineStats.Add(EngineStats.ColHits);
            var result = new SceneCollisionInfo(hit.Target.Id, resultPosition, hit.Normal, hit.Time, hit.Triangle, hit.Surface);
            sourceState.Collisions.Add(result);
            (hit.Target.CollisionState ?? GetCollisionState(hit.Target.Id)).Collisions.Add(result with { Entity = source.Id });

            var collisionPlane = new CollisionPlane(hit.Normal, contact);
            collisionPlane = collisionPlane with { Point = Add(collisionPlane.Point, Multiply(hit.Normal, CollisionEpsilon)) };
            var planeTime = hit.Time;
            var denominator = Dot(hit.Normal, lineDirection);
            if (MathF.Abs(denominator) > Epsilon)
                planeTime = Dot(hit.Normal, Subtract(collisionPlane.Point, lineStart)) / denominator;
            if (planeTime > 0)
            {
                lineStart = Add(lineStart, Multiply(lineDirection, planeTime));
                remainingDistance *= 1 - planeTime;
                remainingXZDistance *= 1 - planeTime;
            }

            if (hit.Rule.Response == CollisionResponseStop)
            {
                destination = lineStart;
                break;
            }

            var nearest = collisionPlane.Nearest(destination);
            if (planeCount == 0)
            {
                destination = nearest;
            }
            else if (planeCount == 1)
            {
                if (planes[0].Distance(nearest) >= 0)
                {
                    destination = nearest;
                    planeCount = 0;
                }
                else if (MathF.Abs(Dot(planes[0].Normal, collisionPlane.Normal)) < 1 - Epsilon)
                {
                    destination = Intersect(planes[0], collisionPlane).Nearest(destination);
                }
                else
                {
                    hitCount = MaxCollisionHits;
                    break;
                }
            }
            else if (planes[0].Distance(nearest) >= 0 && planes[1].Distance(nearest) >= 0)
            {
                destination = nearest;
                planeCount = 0;
            }
            else
            {
                destination = lineStart;
                break;
            }

            var delta = Subtract(destination, lineStart);
            if (Dot(delta, originalDirection) <= 0)
            {
                destination = lineStart;
                break;
            }

            if (hit.Rule.Response == CollisionResponseSlide)
            {
                var distance = Length(delta);
                if (distance <= Epsilon) { destination = lineStart; break; }
                if (distance > remainingDistance) delta = Multiply(delta, remainingDistance / distance);
            }
            else if (hit.Rule.Response == CollisionResponseSlideXZ)
            {
                var distance = Length(new(delta.X, 0, delta.Z));
                if (distance <= Epsilon) { destination = lineStart; break; }
                if (distance > remainingXZDistance) delta = Multiply(delta, remainingXZDistance / distance);
            }

            lineDirection = delta;
            destination = Add(lineStart, delta);
            planes[planeCount++] = collisionPlane;
        }

        if (hitCount == 0) return;
        SetWorldPosition(source, hitCount < MaxCollisionHits ? ScaleY(destination, inverseYScale) : panicPosition);
    }

    private bool FindNearestHit(int sourceId, int sourceType, SceneVector3 lineStart, SceneVector3 lineDirection, float sourceRadius,
        float yScale, out SweepHit nearest)
    {
        nearest = default;
        var found = false;
        var nearestTime = 1f;
        var statFindStart = EngineStats.Now();
        // Conservative swept-volume bounds (scaled space) of the whole start->end segment, padded by the source radius
        // and a safety margin; a target whose bound is disjoint from it cannot produce a hit.
        var broad = !_legacyCollisionPath && EngineOptimizations.Broadphase;
        if (broad)
        {
            var end = Add(lineStart, lineDirection);
            var pad = sourceRadius + BroadMargin(lineStart, end);
            _sweepMin = new(MathF.Min(lineStart.X, end.X) - pad, MathF.Min(lineStart.Y, end.Y) - pad, MathF.Min(lineStart.Z, end.Z) - pad);
            _sweepMax = new(MathF.Max(lineStart.X, end.X) + pad, MathF.Max(lineStart.Y, end.Y) + pad, MathF.Max(lineStart.Z, end.Z) + pad);
            broad = float.IsFinite(pad) && float.IsFinite(_sweepMin.X + _sweepMin.Y + _sweepMin.Z + _sweepMax.X + _sweepMax.Y + _sweepMax.Z);
        }
        _broadOn = broad;
        ReadOnlySpan<CollisionRule> rules = _legacyCollisionPath || !EngineOptimizations.CollState
            ? CollectionsMarshal.AsSpan(_collisionRules)
            : _rulesBySource[sourceType];
        for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (rule.SourceType != sourceType) continue;
            var destinations = _collisionEntitiesByType[rule.DestinationType];
            if (destinations is null) { EngineStats.Add(EngineStats.ColPairsRejType); continue; }
            var targets = CollectionsMarshal.AsSpan(destinations);
            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                var target = targets[targetIndex];
                if (target.Id == sourceId) { EngineStats.Add(EngineStats.ColPairsRejType); continue; }
                var targetState = target.CollisionState ?? GetCollisionState(target.Id);
                var statTryStart = EngineStats.Now();
                EngineStats.Add(EngineStats.ColPairs);
                var hitFound = TryHit(rule, target, targetState, lineStart, lineDirection, sourceRadius, yScale, nearestTime, broad, out var candidate);
                if (EngineStats.Enabled) _statTry += Stopwatch.GetTimestamp() - statTryStart;
                if (!hitFound) continue;
                nearest = candidate;
                nearestTime = candidate.Time;
                found = true;
            }
        }
        _broadOn = false;
        if (EngineStats.Enabled) _statFind += Stopwatch.GetTimestamp() - statFindStart;
        return found;
    }

    /// <param name="broad">Apply the swept-volume broadphase; only the collision sweep sets this, picking passes false.</param>
    private bool TryHit(CollisionRule rule, Node target, CollisionEntityState targetState, SceneVector3 lineStart,
        SceneVector3 lineDirection, float radius, float yScale, float maxTime, bool broad, out SweepHit hit)
    {
        hit = default;
        var targetTransform = GetWorldTransform(target);
        if (broad && BroadReject(rule.Method, targetTransform, targetState, yScale))
        {
            EngineStats.Add(EngineStats.ColPairsRejBound);
            return false;
        }
        if (rule.Method == CollisionMethodSphere)
        {
            EngineStats.Add(EngineStats.ColSphereTests);
            var centre = ScaleY(targetTransform.Position, yScale);
            if (!TrySphere(lineStart, lineDirection, radius + targetState.Radii.X, centre, maxTime, out var time, out var normal)) return false;
            hit = new(target, rule, time, normal, ushort.MaxValue);
            return true;
        }

        if (rule.Method == CollisionMethodBox)
        {
            EngineStats.Add(EngineStats.ColBoxTests);
            if (!TryBox(lineStart, lineDirection, radius, yScale, targetTransform, targetState, maxTime, out var time, out var localNormal)) return false;
            var scaled = ScaleTransformY(targetTransform, yScale);
            var basis = new Matrix3(Normalize(scaled.BasisX), Normalize(scaled.BasisY), Normalize(scaled.BasisZ));
            var normal = Normalize(basis.Transform(localNormal));
            hit = new(target, rule, time, normal, ushort.MaxValue);
            return true;
        }

        if (rule.Method == CollisionMethodPolygon)
        {
            var scaled = ScaleTransformY(targetTransform, yScale);
            if (targetState.InfinitePlane)
            {
                EngineStats.Add(EngineStats.ColPlaneTests);
                var matrix = new Matrix3(scaled.BasisX, scaled.BasisY, scaled.BasisZ);
                var determinant = Dot(matrix.I, Cross(matrix.J, matrix.K));
                if (MathF.Abs(determinant) <= Epsilon) return false;
                var localStart = matrix.InverseTransform(Subtract(lineStart, scaled.Position));
                var localDirection = matrix.InverseTransform(lineDirection);
                if (MathF.Abs(localDirection.Y) <= Epsilon) return false;

                // Match PlaneModel::collide: offset the local plane by the source radius,
                // then transform its +Y normal with the target cofactor matrix.
                var time = (radius - localStart.Y) / localDirection.Y;
                if (time >= maxTime) return false;
                var normal = Normalize(CofactorTransform(scaled, new(0, 1, 0)));
                var contact = Add(lineStart, Multiply(lineDirection, time));
                if (!UpdateCollision(lineStart, lineDirection, time, normal, contact, maxTime)) return false;
                hit = new(target, rule, time, normal, ushort.MaxValue);
                return true;
            }

            var found = false;
            var bestTime = maxTime;
            var bestNormal = default(SceneVector3);
            var bestTriangle = (int)ushort.MaxValue;
            var bestSurface = 0;
            var candidates = targetState.TriangleCandidates;
            var triangleCollider = targetState.TriangleCollider ??= new SceneTriangleCollider(targetState.Triangles);
            var candidateCount = targetState.Triangles.Length;
            if (!ForceLinearPolygonSearchForTesting)
            {
                var statQueryStart = EngineStats.Now();
                triangleCollider.Query(scaled, lineStart, lineDirection, radius, candidates);
                if (EngineStats.Enabled) _statQuery += Stopwatch.GetTimestamp() - statQueryStart;
                candidateCount = candidates.Count;
            }
            if (EngineStats.Enabled)
            {
                EngineStats.Add(EngineStats.ColBvhCands, candidateCount);
                EngineStats.Add(EngineStats.ColTriTests, candidateCount);
                if (candidateCount == 0) EngineStats.Add(EngineStats.ColPairsRejBound);
            }

            for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                var index = ForceLinearPolygonSearchForTesting ? candidateIndex : candidates[candidateIndex];
                var triangle = targetState.Triangles[index];
                var a = scaled.TransformPoint(triangle.A);
                var b = scaled.TransformPoint(triangle.B);
                var c = scaled.TransformPoint(triangle.C);
                if (!TryTriangle(lineStart, lineDirection, radius, a, b, c, bestTime, out var time, out var normal)) continue;
                found = true;
                bestTime = time;
                bestNormal = normal;
                bestTriangle = triangle.Index < 0 ? index : triangle.Index;
                bestSurface = triangle.Surface;
            }
            if (!found) return false;
            hit = new(target, rule, bestTime, bestNormal, bestTriangle, bestSurface);
            return true;
        }

        return false;
    }

    private bool _broadOn;
    private SceneVector3 _sweepMin, _sweepMax;

    private static float BroadMargin(SceneVector3 a, SceneVector3 b) =>
        0.01f + 1e-4f * MathF.Max(MathF.Max(MathF.Abs(a.X), MathF.Abs(a.Y)), MathF.Max(MathF.Abs(a.Z),
            MathF.Max(MathF.Abs(b.X), MathF.Max(MathF.Abs(b.Y), MathF.Abs(b.Z)))));

    private bool Disjoint(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        // NaN compares false everywhere, so a non-finite bound is never rejected.
        return maxX < _sweepMin.X || minX > _sweepMax.X || maxY < _sweepMin.Y || minY > _sweepMax.Y
            || maxZ < _sweepMin.Z || minZ > _sweepMax.Z;
    }

    /// <summary>True when the target's volume (scaled space) provably cannot be hit by the current sweep.</summary>
    private bool BroadReject(int method, SceneTransform world, CollisionEntityState state, float yScale)
    {
        var pos = world.Position;
        var cy = pos.Y * yScale;
        if (method == CollisionMethodSphere)
        {
            var e = state.Radii.X + BroadMargin(pos, pos);
            return Disjoint(pos.X - e, cy - e, pos.Z - e, pos.X + e, cy + e, pos.Z + e);
        }
        if (method == CollisionMethodBox)
        {
            // The narrow phase uses unit-length basis columns, so |M * corner| <= |x| + |y| + |z| of the corner.
            var min = state.BoxMinimum; var max = state.BoxMaximum;
            var e = MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)) + MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y))
                + MathF.Max(MathF.Abs(min.Z), MathF.Abs(max.Z)) + BroadMargin(pos, pos);
            return Disjoint(pos.X - e, cy - e, pos.Z - e, pos.X + e, cy + e, pos.Z + e);
        }
        if (method == CollisionMethodPolygon && !state.InfinitePlane)
        {
            if (!(state.BoundValid && state.BoundTransform == world))
            {
                var collider = state.TriangleCollider ??= new SceneTriangleCollider(state.Triangles);
                if (!collider.TryGetLocalBounds(out var local)) return false;
                var mn = new SceneVector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                var mx = new SceneVector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (var corner = 0; corner < 8; corner++)
                {
                    var p = world.TransformPoint(new((corner & 1) == 0 ? local.Minimum.X : local.Maximum.X,
                        (corner & 2) == 0 ? local.Minimum.Y : local.Maximum.Y, (corner & 4) == 0 ? local.Minimum.Z : local.Maximum.Z));
                    mn = new(MathF.Min(mn.X, p.X), MathF.Min(mn.Y, p.Y), MathF.Min(mn.Z, p.Z));
                    mx = new(MathF.Max(mx.X, p.X), MathF.Max(mx.Y, p.Y), MathF.Max(mx.Z, p.Z));
                }
                state.BoundMin = mn; state.BoundMax = mx; state.BoundTransform = world; state.BoundValid = true;
            }
            var m = BroadMargin(state.BoundMin, state.BoundMax);
            var y0 = state.BoundMin.Y * yScale; var y1 = state.BoundMax.Y * yScale;
            return Disjoint(state.BoundMin.X - m, MathF.Min(y0, y1) - m, state.BoundMin.Z - m,
                state.BoundMax.X + m, MathF.Max(y0, y1) + m, state.BoundMax.Z + m);
        }
        return false;
    }

    private bool TryBox(SceneVector3 lineStart, SceneVector3 lineDirection, float radius, float yScale,
        SceneTransform targetTransform, CollisionEntityState targetState, float maxTime, out float time, out SceneVector3 normal)
    {
        var scaled = ScaleTransformY(targetTransform, yScale);
        var matrix = new Matrix3(Normalize(scaled.BasisX), Normalize(scaled.BasisY), Normalize(scaled.BasisZ));
        var localStart = matrix.InverseTransform(Subtract(lineStart, scaled.Position));
        var localDirection = matrix.InverseTransform(lineDirection);
        var box = new CollisionBox(targetState.BoxMinimum, targetState.BoxMaximum);
        if (TryBoxLocal(localStart, localDirection, radius, box, maxTime, out time, out var localNormal))
        {
            normal = localNormal;
            return true;
        }
        normal = default;
        return false;
    }

    private static bool TrySphere(SceneVector3 start, SceneVector3 direction, float radius, SceneVector3 centre,
        float maxTime, out float time, out SceneVector3 normal)
    {
        var relative = Subtract(start, centre);
        var a = Dot(direction, direction);
        if (a == 0) { time = 0; normal = default; return false; }
        var b = 2 * Dot(relative, direction);
        var c = Dot(relative, relative) - radius * radius;
        var discriminant = b * b - 4 * a * c;
        if (discriminant < 0) { time = 0; normal = default; return false; }
        time = (-b - MathF.Sqrt(discriminant)) / (2 * a);
        var point = Add(start, Multiply(direction, time));
        normal = Normalize(Subtract(point, centre));
        return UpdateCollision(start, direction, time, normal, point, maxTime);
    }

    private static bool TryTriangle(SceneVector3 start, SceneVector3 direction, float radius,
        SceneVector3 a, SceneVector3 b, SceneVector3 c, float maxTime, out float time, out SceneVector3 normal)
    {
        normal = Normalize(Cross(Subtract(b, a), Subtract(c, a)));
        if (Dot(normal, direction) >= 0) { time = 0; return false; }
        var denominator = Dot(normal, direction);
        time = (radius - Dot(normal, Subtract(start, a))) / denominator;
        var contact = Add(start, Multiply(direction, time));
        if (time >= 0 && time <= maxTime && InsideTriangle(contact, a, b, c, normal)
            && UpdateCollision(start, direction, time, normal, contact, maxTime)) return true;
        if (radius <= 0) return false;

        var found = false;
        var bestTime = maxTime;
        var bestNormal = normal;
        if (TryTriangleEdge(start, direction, radius, a, b, bestTime, out var edgeTime, out var edgeNormal))
        { found = true; bestTime = edgeTime; bestNormal = edgeNormal; }
        if (TryTriangleEdge(start, direction, radius, b, c, bestTime, out edgeTime, out edgeNormal))
        { found = true; bestTime = edgeTime; bestNormal = edgeNormal; }
        if (TryTriangleEdge(start, direction, radius, c, a, bestTime, out edgeTime, out edgeNormal))
        { found = true; bestTime = edgeTime; bestNormal = edgeNormal; }
        time = bestTime;
        normal = bestNormal;
        return found;
    }

    private static bool TryTriangleEdge(SceneVector3 start, SceneVector3 direction, float radius,
        SceneVector3 a, SceneVector3 b, float maxTime, out float time, out SceneVector3 normal)
    {
        var edge = Subtract(b, a);
        var length = Length(edge);
        if (length == 0) { time = 0; normal = default; return false; }
        var axis = Multiply(edge, 1 / length);
        var relative = Subtract(start, a);
        var alongStart = Dot(relative, axis);
        var alongDirection = Dot(direction, axis);
        var radialStart = Subtract(relative, Multiply(axis, alongStart));
        var radialDirection = Subtract(direction, Multiply(axis, alongDirection));
        var qa = Dot(radialDirection, radialDirection);
        var qb = 2 * Dot(radialStart, radialDirection);
        var qc = Dot(radialStart, radialStart) - radius * radius;
        if (qa == 0) { time = 0; normal = default; return false; }
        var discriminant = qb * qb - 4 * qa * qc;
        if (discriminant < 0) { time = 0; normal = default; return false; }
        time = (-qb - MathF.Sqrt(discriminant)) / (2 * qa);
        if (time < 0) { normal = default; return false; }
        var along = alongStart + alongDirection * time;
        if (along < 0)
        {
            if (!TrySphere(start, direction, radius, a, maxTime, out time, out normal)) return false;
            return true;
        }
        if (along > length || time > maxTime) { normal = default; return false; }
        var point = Add(start, Multiply(direction, time));
        var closest = Add(a, Multiply(axis, along));
        normal = Normalize(Subtract(point, closest));
        return UpdateCollision(start, direction, time, normal, point, maxTime);
    }

    private static bool TryBoxLocal(SceneVector3 start, SceneVector3 direction, float radius,
        CollisionBox box, float maxTime, out float time, out SceneVector3 normal)
    {
        Span<SceneVector3> corners = stackalloc SceneVector3[8];
        for (var index = 0; index < corners.Length; index++)
            corners[index] = new((index & 1) == 0 ? box.Minimum.X : box.Maximum.X,
                (index & 2) == 0 ? box.Minimum.Y : box.Maximum.Y,
                (index & 4) == 0 ? box.Minimum.Z : box.Maximum.Z);
        ReadOnlySpan<int> quads = [2, 3, 1, 0, 3, 7, 5, 1, 7, 6, 4, 5, 6, 2, 0, 4, 6, 7, 3, 2, 0, 1, 5, 4];
        var found = false;
        var bestTime = maxTime;
        var bestNormal = default(SceneVector3);
        for (var face = 0; face < 6; face++)
        {
            var v0 = corners[quads[face * 4]];
            var v1 = corners[quads[face * 4 + 1]];
            var v2 = corners[quads[face * 4 + 2]];
            var v3 = corners[quads[face * 4 + 3]];
            var faceNormal = Normalize(Cross(Subtract(v1, v0), Subtract(v2, v0)));
            if (Dot(faceNormal, direction) >= 0) continue;
            var denominator = Dot(faceNormal, direction);
            var candidateTime = (radius - Dot(faceNormal, Subtract(start, v0))) / denominator;
            var point = Add(start, Multiply(direction, candidateTime));
            if (candidateTime >= 0 && candidateTime <= bestTime && InsideQuad(point, v0, v1, v2, v3, faceNormal)
                && UpdateCollision(start, direction, candidateTime, faceNormal, point, bestTime))
            {
                found = true; bestTime = candidateTime; bestNormal = faceNormal;
            }
            if (radius <= 0) continue;
            if (TryTriangleEdge(start, direction, radius, v0, v1, bestTime, out candidateTime, out var edgeNormal))
            { found = true; bestTime = candidateTime; bestNormal = edgeNormal; }
            if (TryTriangleEdge(start, direction, radius, v1, v2, bestTime, out candidateTime, out edgeNormal))
            { found = true; bestTime = candidateTime; bestNormal = edgeNormal; }
            if (TryTriangleEdge(start, direction, radius, v2, v3, bestTime, out candidateTime, out edgeNormal))
            { found = true; bestTime = candidateTime; bestNormal = edgeNormal; }
            if (TryTriangleEdge(start, direction, radius, v3, v0, bestTime, out candidateTime, out edgeNormal))
            { found = true; bestTime = candidateTime; bestNormal = edgeNormal; }
        }
        time = bestTime;
        normal = bestNormal;
        return found;
    }

    private static bool InsideTriangle(SceneVector3 point, SceneVector3 a, SceneVector3 b, SceneVector3 c, SceneVector3 normal) =>
        Dot(Cross(normal, Subtract(b, a)), Subtract(point, a)) >= 0 &&
        Dot(Cross(normal, Subtract(c, b)), Subtract(point, b)) >= 0 &&
        Dot(Cross(normal, Subtract(a, c)), Subtract(point, c)) >= 0;

    private static bool InsideQuad(SceneVector3 point, SceneVector3 a, SceneVector3 b, SceneVector3 c, SceneVector3 d, SceneVector3 normal) =>
        Dot(Cross(normal, Subtract(b, a)), Subtract(point, a)) >= 0 &&
        Dot(Cross(normal, Subtract(c, b)), Subtract(point, b)) >= 0 &&
        Dot(Cross(normal, Subtract(d, c)), Subtract(point, c)) >= 0 &&
        Dot(Cross(normal, Subtract(a, d)), Subtract(point, d)) >= 0;

    private static bool UpdateCollision(SceneVector3 start, SceneVector3 direction, float time,
        SceneVector3 normal, SceneVector3 contact, float maxTime) =>
        time <= maxTime && Dot(normal, direction) < 0 && Dot(normal, Subtract(start, contact)) >= -CollisionEpsilon;

    private static CollisionPlane Intersect(CollisionPlane first, CollisionPlane second)
    {
        var direction = Cross(first.Normal, second.Normal);
        var denominator = Dot(direction, direction);
        var point = Add(Multiply(Cross(second.Normal, direction), Dot(first.Normal, first.Point)),
            Multiply(Cross(direction, first.Normal), Dot(second.Normal, second.Point)));
        return new(Normalize(direction), Multiply(point, 1 / denominator));
    }

    private static SceneTransform ScaleTransformY(SceneTransform transform, float scale) => new(
        ScaleY(transform.BasisX, scale), ScaleY(transform.BasisY, scale), ScaleY(transform.BasisZ, scale), ScaleY(transform.Position, scale));

    private static bool NearlyEqual(SceneVector3 left, SceneVector3 right) =>
        MathF.Abs(left.X - right.X) <= Epsilon && MathF.Abs(left.Y - right.Y) <= Epsilon && MathF.Abs(left.Z - right.Z) <= Epsilon;

    private static SceneVector3 ScaleY(SceneVector3 value, float scale) => new(value.X, value.Y * scale, value.Z);
    private sealed class CollisionEntityState
    {
        public int Type { get; set; }
        public SceneVector3 Radii { get; set; } = new(1, 1, 1);
        public SceneVector3 BoxMinimum { get; set; } = new(-1, -1, -1);
        public SceneVector3 BoxMaximum { get; set; } = new(1, 1, 1);
        public int PickMode { get; set; }
        public bool PickObscurer { get; set; }
        public bool InfinitePlane { get; set; }
        public SceneCollisionTriangle[] Triangles { get; set; } = [];
        public SceneTriangleCollider? TriangleCollider { get; set; }
        public bool BoundValid;
        public SceneTransform BoundTransform;
        public SceneVector3 BoundMin, BoundMax;
        public List<int> TriangleCandidates { get; } = [];
        public SceneTransform PreviousTransform { get; set; }
        public bool HasPreviousTransform { get; set; }
        public List<SceneCollisionInfo> Collisions { get; } = [];
    }

    private readonly record struct CollisionRule(int SourceType, int DestinationType, int Method, int Response);
    private readonly record struct SweepHit(Node Target, CollisionRule Rule, float Time, SceneVector3 Normal, int Triangle, int Surface = 0);
    private readonly record struct CollisionBox(SceneVector3 Minimum, SceneVector3 Maximum);
    private readonly record struct CollisionPlane(SceneVector3 Normal, SceneVector3 Point)
    {
        public float Distance(SceneVector3 value) => Dot(Normal, Subtract(value, Point));
        public SceneVector3 Nearest(SceneVector3 value) => Subtract(value, Multiply(Normal, Distance(value)));
    }
}
