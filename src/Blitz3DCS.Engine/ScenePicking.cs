namespace Blitz3DCS.Engine;

/// <summary>Legacy pick results retained by a scene until the next pick query.</summary>
public sealed partial class SceneGraph
{
    private readonly List<Node> _enabledPickingNodes = [];
    private int _pickedEntity;
    private SceneVector3 _pickedPosition;
    private SceneVector3 _pickedNormal;

    /// <summary>Sets pick geometry (0 none, 1 sphere, 2 polygon, 3 box) and LOS obstruction behavior.</summary>
    public void EntityPickMode(int entity, int mode, bool obscurer = true)
    {
        var state = GetCollisionState(entity);
        EngineStats.Add(EngineStats.SelBumpPick);
        if (state.PickMode != mode || !EngineOptimizations.IncrSelection) NoteSelectionChanged(entity);
        state.PickMode = mode;
        state.PickObscurer = obscurer;
    }

    /// <summary>Whether this entity needs polygon geometry for collision or picking queries.</summary>
    public bool RequiresPolygonCollisionOrPicking(int entity)
    {
        CollisionEntityState state;
        if (!EngineOptimizations.CollState) state = GetCollisionState(entity);
        else if (!_collisionEntities.TryGetValue(entity, out state!)) return false; // stateless: type 0, no pick mode
        if (state.PickMode == CollisionMethodPolygon) return true;
        if (state.Type == 0) return false;
        foreach (var rule in _collisionRules)
            if (rule.DestinationType == state.Type && rule.Method == CollisionMethodPolygon) return true;
        return false;
    }

    /// <summary>Returns the closest enabled entity intersected by a finite segment and stores legacy picked fields.</summary>
    public int LinePick(float x, float y, float z, float dx, float dy, float dz, float radius = 0)
    {
        var start = new SceneVector3(x, y, z);
        var direction = new SceneVector3(dx, dy, dz);
        var bestTime = 1f;
        var bestEntity = 0;
        var bestNormal = _pickedNormal;
        var bestPosition = _pickedPosition;

        _enabledPickingNodes.Clear();
        foreach (var node in _nodes.Values)
            if (node.Parent is null) CollectEnabled(node, _enabledPickingNodes);

        foreach (var target in _enabledPickingNodes)
        {
            var state = GetCollisionState(target.Id);
            if (state.PickMode == 0) continue;
            var rule = new CollisionRule(0, 0, state.PickMode, CollisionResponseNone);
            if (!TryHit(rule, target, state, start, direction, radius, 1, bestTime, false, out var hit)) continue;
            bestTime = hit.Time;
            bestEntity = target.Id;
            bestNormal = hit.Normal;
            bestPosition = Subtract(Add(start, Multiply(direction, hit.Time)), Multiply(hit.Normal, radius));
        }

        _pickedEntity = bestEntity;
        if (bestEntity != 0)
        {
            _pickedNormal = bestNormal;
            _pickedPosition = bestPosition;
        }
        return bestEntity;
    }

    /// <summary>Projects a finite pick segment along an entity's world forward axis.</summary>
    public int EntityPick(int entity, float range)
    {
        var transform = GetWorldTransform(GetNode(entity));
        return LinePick(transform.Position.X, transform.Position.Y, transform.Position.Z,
            transform.BasisZ.X * range, transform.BasisZ.Y * range, transform.BasisZ.Z * range);
    }

    /// <summary>Returns whether the segment between two world positions has no enabled pick-obscurer hit.</summary>
    public bool EntityVisible(int source, int destination)
    {
        var start = GetWorldTransform(GetNode(source)).Position;
        var end = GetWorldTransform(GetNode(destination)).Position;
        var direction = Subtract(end, start);

        _enabledPickingNodes.Clear();
        foreach (var node in _nodes.Values)
            if (node.Parent is null) CollectEnabled(node, _enabledPickingNodes);

        foreach (var target in _enabledPickingNodes)
        {
            if (target.Id == source || target.Id == destination) continue;
            var state = GetCollisionState(target.Id);
            if (state.PickMode == 0 || !state.PickObscurer) continue;
            var rule = new CollisionRule(0, 0, state.PickMode, CollisionResponseNone);
            if (TryHit(rule, target, state, start, direction, 0, 1, 1, false, out _)) return false;
        }
        return true;
    }

    public int PickedEntity => _pickedEntity;
    public float PickedX => _pickedPosition.X;
    public float PickedY => _pickedPosition.Y;
    public float PickedZ => _pickedPosition.Z;
    public float PickedNX => _pickedNormal.X;
    public float PickedNY => _pickedNormal.Y;
    public float PickedNZ => _pickedNormal.Z;
}
