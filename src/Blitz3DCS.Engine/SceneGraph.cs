namespace Blitz3DCS.Engine;

/// <summary>Owns scene entities and their parent/local/world transforms.</summary>
public sealed partial class SceneGraph
{
    private readonly Dictionary<int, Node> _nodes = [];
    private int _nextId = 1;

    // Root entities ordered by dictionary slot, i.e. the order `_nodes.Values` enumerates them. Slots are assigned with
    // the same LIFO free-list policy as Dictionary<TKey,TValue>, so iterating _roots matches the original root scan.
    private readonly List<Node> _rootList = [];
    private readonly List<int> _freeSlots = [];
    private int _nextSlot;
    /// <summary>Bumped by every change that can alter enabled-node order, collision types or rules.</summary>
    private int _structureVersion;

    private void RegisterNode(Node node)
    {
        if (_freeSlots.Count > 0)
        {
            node.Slot = _freeSlots[^1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
        }
        else node.Slot = _nextSlot++;
        if (node.Parent is null) AddRoot(node);
        _structureVersion++;
    }

    private void UnregisterNode(Node node)
    {
        if (node.InRoots) RemoveRoot(node);
        _freeSlots.Add(node.Slot);
        _structureVersion++;
    }

    // O(1) add/remove: roots are indexed by slot and the slot-ordered list is rebuilt lazily on first read after a change.
    private Node?[] _rootBySlot = new Node?[64];
    private bool _rootsDirty;

    /// <summary>Slot-ordered root list (the original dictionary iteration order), rebuilt on demand.</summary>
    private List<Node> _roots
    {
        get
        {
            if (_rootsDirty)
            {
                _rootList.Clear();
                var slots = _rootBySlot;
                var limit = Math.Min(_nextSlot, slots.Length);
                for (var i = 0; i < limit; i++)
                    if (slots[i] is { } root) _rootList.Add(root);
                _rootsDirty = false;
            }
            return _rootList;
        }
    }

    private void AddRoot(Node node)
    {
        if (node.Slot >= _rootBySlot.Length) Array.Resize(ref _rootBySlot, Math.Max(node.Slot + 1, _rootBySlot.Length * 2));
        _rootBySlot[node.Slot] = node;
        _rootsDirty = true;
        node.InRoots = true;
    }

    private void RemoveRoot(Node node)
    {
        _rootBySlot[node.Slot] = null;
        _rootsDirty = true;
        node.InRoots = false;
    }

    /// <summary>Creates an identity pivot. A nonzero parent ID attaches it as that parent's last child.</summary>
    public int CreatePivot(int parent = 0)
    {
        var parentNode = parent == 0 ? null : GetNode(parent);
        if (_nextId == int.MaxValue)
            throw new InvalidOperationException("The scene has exhausted its positive entity ID range.");

        var node = new Node(_nextId++);
        EngineStats.Add(EngineStats.XformCreated);
        node.Parent = parentNode;
        parentNode?.Children.Add(node);
        _nodes.Add(node.Id, node);
        RegisterNode(node);
        return node.Id;
    }

    /// <summary>Frees an entity and all descendants. IDs are never reused.</summary>
    public void FreeEntity(int entity)
    {
        var root = GetNode(entity);
        root.Parent?.Children.Remove(root);
        root.Parent = null;

        var pending = new Stack<Node>();
        var removed = new List<Node>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            removed.Add(current);
            foreach (var child in current.Children)
                pending.Push(child);
        }

        foreach (var current in removed)
        {
            ForgetCollisionState(current.Id);
            _nodes.Remove(current.Id);
            UnregisterNode(current);
            current.Parent = null;
            current.Children.Clear();
        }
    }

    /// <summary>
    /// Copies a transform-only entity subtree, preserving names, local transforms, and child order.
    /// The copy is attached to <paramref name="parent"/> without changing its local transforms.
    /// Graphics resources and other entity-specific state are owned by higher-level backends.
    /// </summary>
    public int CopyEntity(int entity, int parent = 0)
    {
        var source = GetNode(entity);
        var parentNode = parent == 0 ? null : GetNode(parent);

        // Snapshot before attaching anything: parent may be a descendant of the source.
        var sourceNodes = GetSubtreeNodes(source);
        if ((long)_nextId + sourceNodes.Count > int.MaxValue)
            throw new InvalidOperationException("The scene has exhausted its positive entity ID range.");

        var copies = new Dictionary<Node, Node>(sourceNodes.Count);
        var nextId = _nextId;
        foreach (var original in sourceNodes)
        {
            copies.Add(original, new Node(nextId++)
            {
                Name = original.Name,
                LocalPosition = original.LocalPosition,
                LocalScale = original.LocalScale,
                LocalRotation = original.LocalRotation
            });
        }

        foreach (var original in sourceNodes)
        {
            var copy = copies[original];
            if (ReferenceEquals(original, source))
            {
                copy.Parent = parentNode;
            }
            else
            {
                var copiedParent = copies[original.Parent!];
                copy.Parent = copiedParent;
                copiedParent.Children.Add(copy);
            }
        }

        var rootCopy = copies[source];
        parentNode?.Children.Add(rootCopy);
        foreach (var copy in copies.Values)
        {
            _nodes.Add(copy.Id, copy);
            RegisterNode(copy);
        }
        foreach (var original in sourceNodes)
            CopyCollisionState(original.Id, copies[original].Id);
        _nextId = nextId;
        return rootCopy.Id;
    }

    /// <summary>Returns an entity subtree in depth-first insertion order, with its root first.</summary>
    public int[] GetSubtreeEntityIds(int entity) => GetSubtreeNodes(GetNode(entity)).Select(node => node.Id).ToArray();

    internal int[] GetRootEntityIds()
    {
        if (!EngineOptimizations.Traversal) return _nodes.Values.Where(node => node.Parent is null).Select(node => node.Id).ToArray();
        var ids = new int[_roots.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = _roots[i].Id;
        return ids;
    }

    public void PositionEntity(int entity, float x, float y, float z, bool global = false)
    {
        var node = GetNode(entity);
        var position = new SceneVector3(x, y, z);
        if (global) SetWorldPosition(node, position);
        else SetLocalPosition(node, position);
    }

    public void TranslateEntity(int entity, float x, float y, float z, bool global = false)
    {
        var node = GetNode(entity);
        var delta = new SceneVector3(x, y, z);
        if (global) SetWorldPosition(node, Add(GetWorldTransform(node).Position, delta));
        else SetLocalPosition(node, Add(node.LocalPosition, delta));
    }

    public void ScaleEntity(int entity, float x, float y, float z, bool global = false)
    {
        var node = GetNode(entity);
        var scale = new SceneVector3(x, y, z);
        if (global) SetWorldScale(node, scale);
        else SetLocalScale(node, scale);
    }

    public void RotateEntity(int entity, float pitch, float yaw, float roll, bool global = false)
    {
        var node = GetNode(entity);
        var rotation = Rotation.FromEulerDegrees(pitch, yaw, roll);
        if (global) SetWorldRotation(node, rotation);
        else SetLocalRotation(node, rotation);
    }

    public void MoveEntity(int entity, float x, float y, float z)
    {
        var node = GetNode(entity);
        var movement = node.LocalRotation.Rotate(new SceneVector3(x, y, z));
        SetLocalPosition(node, Add(node.LocalPosition, movement));
    }

    public void TurnEntity(int entity, float pitch, float yaw, float roll, bool global = false)
    {
        var node = GetNode(entity);
        var rotation = Rotation.FromEulerDegrees(pitch, yaw, roll);
        if (global) SetWorldRotation(node, rotation * GetWorldRotation(node));
        else SetLocalRotation(node, node.LocalRotation * rotation);
    }

    public float EntityX(int entity, bool global = false) => global
        ? GetWorldTransform(GetNode(entity)).Position.X
        : GetNode(entity).LocalPosition.X;

    public float EntityY(int entity, bool global = false) => global
        ? GetWorldTransform(GetNode(entity)).Position.Y
        : GetNode(entity).LocalPosition.Y;

    public float EntityZ(int entity, bool global = false) => global
        ? GetWorldTransform(GetNode(entity)).Position.Z
        : GetNode(entity).LocalPosition.Z;

    public float EntityPitch(int entity, bool global = false) => GetRotation(node: GetNode(entity), global).PitchDegrees;
    public float EntityYaw(int entity, bool global = false) => GetRotation(node: GetNode(entity), global).YawDegrees;
    public float EntityRoll(int entity, bool global = false) => GetRotation(node: GetNode(entity), global).RollDegrees;

    public float EntityScaleX(int entity, bool global = false) => (global ? GetWorldScale(GetNode(entity)) : GetNode(entity).LocalScale).X;
    public float EntityScaleY(int entity, bool global = false) => (global ? GetWorldScale(GetNode(entity)) : GetNode(entity).LocalScale).Y;
    public float EntityScaleZ(int entity, bool global = false) => (global ? GetWorldScale(GetNode(entity)) : GetNode(entity).LocalScale).Z;

    /// <summary>Reparents an entity. By default, its world transform is preserved.</summary>
    public void EntityParent(int entity, int parent, bool global = true)
    {
        var node = GetNode(entity);
        var parentNode = parent == 0 ? null : GetNode(parent);
        if (ReferenceEquals(node.Parent, parentNode)) return;
        for (var ancestor = parentNode; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, node))
                throw new ArgumentException("An entity cannot be parented to itself or one of its descendants.", nameof(parent));

        var worldTransform = global ? GetWorldTransform(node) : default;
        node.Parent?.Children.Remove(node);
        if (node.InRoots) RemoveRoot(node);
        node.Parent = parentNode;
        if (parentNode is null) AddRoot(node);
        parentNode?.Children.Add(node);
        _structureVersion++;
        EngineStats.Add(EngineStats.XformInvParent);
        InvalidateWorld(node, true);
        if (global) SetWorldTransform(node, worldTransform);
    }

    public int GetParent(int entity) => GetNode(entity).Parent?.Id ?? 0;
    public int CountChildren(int entity) => GetNode(entity).Children.Count;

    /// <summary>Returns the one-based child at <paramref name="index"/>, or zero if out of range.</summary>
    public int GetChild(int entity, int index)
    {
        var children = GetNode(entity).Children;
        return index > 0 && index <= children.Count ? children[index - 1].Id : 0;
    }

    public void NameEntity(int entity, string name) => GetNode(entity).Name = name ?? throw new ArgumentNullException(nameof(name));
    public string EntityName(int entity) => GetNode(entity).Name;

    /// <summary>Searches this entity and its descendants in depth-first insertion order; returns zero if absent.</summary>
    public int FindChild(int entity, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var pending = new Stack<Node>();
        pending.Push(GetNode(entity));
        while (pending.TryPop(out var node))
        {
            if (string.Equals(node.Name, name, StringComparison.Ordinal)) return node.Id;
            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push(node.Children[index]);
        }
        return 0;
    }

    public float EntityDistance(int source, int destination)
    {
        var delta = Subtract(GetWorldTransform(GetNode(source)).Position, GetWorldTransform(GetNode(destination)).Position);
        return MathF.Sqrt(Dot(delta, delta));
    }

    /// <summary>Gets the composed affine transform using column basis vectors.</summary>
    public SceneTransform GetWorldTransform(int entity) => GetWorldTransform(GetNode(entity));

    private Node GetNode(int id) => id > 0 && _nodes.TryGetValue(id, out var node)
        ? node
        : throw new KeyNotFoundException($"Entity handle {id} does not exist in this scene.");

    private static List<Node> GetSubtreeNodes(Node root)
    {
        var result = new List<Node>();
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            result.Add(current);
            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }
        return result;
    }

    private static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static bool SameBits(SceneVector3 a, SceneVector3 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);
    private static bool SameBits(Rotation a, Rotation b) => SameBits(a.W, b.W) && SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);

    // A setter that stores the exact bit pattern already present changes nothing observable, so it must not dirty
    // the node or its descendants (original behaviour under BLITZCS_ENGINE_NOOPT=1).
    private static bool NoOp(bool same)
    {
        if (!same || !EngineOptimizations.SetterGuard) return false;
        EngineStats.Add(EngineStats.XformNoop);
        return true;
    }

    private static void SetLocalPosition(Node node, SceneVector3 position)
    {
        if (NoOp(SameBits(node.LocalPosition, position))) return;
        node.LocalPosition = position;
        InvalidateLocal(node);
    }

    private static void SetLocalScale(Node node, SceneVector3 scale)
    {
        if (NoOp(SameBits(node.LocalScale, scale))) return;
        node.LocalScale = scale;
        InvalidateLocal(node);
    }

    private static void SetLocalRotation(Node node, Rotation rotation)
    {
        var normalized = rotation.Normalized();
        if (NoOp(SameBits(node.LocalRotation, normalized))) return;
        node.LocalRotation = normalized;
        InvalidateLocal(node);
    }

    private static void SetWorldPosition(Node node, SceneVector3 position)
    {
        if (node.Parent is null) SetLocalPosition(node, position);
        else SetLocalPosition(node, GetWorldTransform(node.Parent).InverseTransformPoint(position));
    }

    private static void SetWorldScale(Node node, SceneVector3 scale)
    {
        SetLocalScale(node, node.Parent is null ? scale : Divide(scale, GetWorldScale(node.Parent)));
    }

    private static void SetWorldRotation(Node node, Rotation rotation)
    {
        SetLocalRotation(node, node.Parent is null ? rotation : GetWorldRotation(node.Parent).Conjugate() * rotation);
    }

    private static void SetWorldTransform(Node node, SceneTransform transform)
    {
        if (node.Parent is not null)
            transform = GetWorldTransform(node.Parent).InverseTransform(transform);
        SetLocalTransform(node, transform);
    }

    private static void SetLocalTransform(Node node, SceneTransform transform)
    {
        node.LocalPosition = transform.Position;
        node.LocalScale = new(Length(transform.BasisX), Length(transform.BasisY), Length(transform.BasisZ));
        node.LocalRotation = Rotation.FromMatrix(new Matrix3(transform.BasisX, transform.BasisY, transform.BasisZ));
        InvalidateLocal(node);
    }

    private static void InvalidateLocal(Node node)
    {
        node.LocalDirty = true;
        EngineStats.Add(EngineStats.XformInvSelf);
        InvalidateWorld(node, true);
    }

    private static void InvalidateWorld(Node node, bool self = false)
    {
        if (node.WorldDirty) return;
        node.WorldDirty = true;
        node.CollisionState?.PickEntry?.MarkDirty();
        if (!self) EngineStats.Add(EngineStats.XformInvAncestor);
        foreach (var child in node.Children) InvalidateWorld(child);
    }

    private static SceneTransform GetLocalTransform(Node node)
    {
        if (!node.LocalDirty) return node.LocalTransform;
        var matrix = node.LocalRotation.ToMatrix();
        node.LocalTransform = new(
            Multiply(matrix.I, node.LocalScale.X),
            Multiply(matrix.J, node.LocalScale.Y),
            Multiply(matrix.K, node.LocalScale.Z),
            node.LocalPosition);
        node.LocalDirty = false;
        return node.LocalTransform;
    }

    private static SceneTransform GetWorldTransform(Node node)
    {
        EngineStats.Add(EngineStats.XformTotal);
        if (!node.WorldDirty) return node.WorldTransform;
        EngineStats.Add(EngineStats.XformDirty);
        var local = GetLocalTransform(node);
        if (node.Parent is null) node.WorldTransform = local;
        else node.WorldTransform = Compose(GetWorldTransform(node.Parent), local);
        node.WorldDirty = false;
        return node.WorldTransform;
    }

    private static Rotation GetWorldRotation(Node node) => node.Parent is null
        ? node.LocalRotation
        : GetWorldRotation(node.Parent) * node.LocalRotation;

    private static SceneVector3 GetWorldScale(Node node) => node.Parent is null
        ? node.LocalScale
        : Multiply(GetWorldScale(node.Parent), node.LocalScale);

    private static Rotation GetRotation(Node node, bool global) => global ? GetWorldRotation(node) : node.LocalRotation;

    private static SceneTransform Compose(SceneTransform parent, SceneTransform local)
    {
        var matrix = new Matrix3(parent.BasisX, parent.BasisY, parent.BasisZ);
        return new(
            matrix.Transform(local.BasisX),
            matrix.Transform(local.BasisY),
            matrix.Transform(local.BasisZ),
            Add(matrix.Transform(local.Position), parent.Position));
    }

    private sealed class Node(int id)
    {
        public int Id { get; } = id;
        public string Name { get; set; } = string.Empty;
        public Node? Parent { get; set; }
        public List<Node> Children { get; } = [];
        public SceneVector3 LocalPosition { get; set; }
        public SceneVector3 LocalScale { get; set; } = new(1, 1, 1);
        public Rotation LocalRotation { get; set; } = Rotation.Identity;
        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public bool LocalDirty { get; set; } = true;
        public bool WorldDirty { get; set; } = true;
        public SceneTransform LocalTransform { get; set; }
        public SceneTransform WorldTransform { get; set; }
        internal CollisionEntityState? CollisionState;
        public int Slot;
        public bool InRoots;
    }

    private readonly record struct Matrix3(SceneVector3 I, SceneVector3 J, SceneVector3 K)
    {
        public SceneVector3 Transform(SceneVector3 value) => Add(Add(Multiply(I, value.X), Multiply(J, value.Y)), Multiply(K, value.Z));

        public SceneVector3 InverseTransform(SceneVector3 value)
        {
            var jk = Cross(J, K);
            var ki = Cross(K, I);
            var ij = Cross(I, J);
            var determinant = Dot(I, jk);
            return new(Dot(value, jk) / determinant, Dot(value, ki) / determinant, Dot(value, ij) / determinant);
        }

        public Matrix3 InverseTransform(Matrix3 value) => new(
            InverseTransform(value.I), InverseTransform(value.J), InverseTransform(value.K));
    }

    private readonly record struct Rotation(float W, float X, float Y, float Z)
    {
        public static Rotation Identity => new(1, 0, 0, 0);
        private SceneVector3 Vector => new(X, Y, Z);
        public Rotation Conjugate() => new(W, -X, -Y, -Z);
        public Rotation Normalized()
        {
            var length = MathF.Sqrt(W * W + X * X + Y * Y + Z * Z);
            return new(W / length, X / length, Y / length, Z / length);
        }

        // The source's quaternion product uses the reverse cross-product order.
        public static Rotation operator *(Rotation left, Rotation right) => new(
            left.W * right.W - Dot(left.Vector, right.Vector),
            Cross(right.Vector, left.Vector).X + right.X * left.W + left.X * right.W,
            Cross(right.Vector, left.Vector).Y + right.Y * left.W + left.Y * right.W,
            Cross(right.Vector, left.Vector).Z + right.Z * left.W + left.Z * right.W);

        public SceneVector3 Rotate(SceneVector3 value) => (this * new Rotation(0, value.X, value.Y, value.Z) * Conjugate()).Vector;

        public Matrix3 ToMatrix()
        {
            var xx = X * X; var yy = Y * Y; var zz = Z * Z;
            var xy = X * Y; var xz = X * Z; var yz = Y * Z;
            var wx = W * X; var wy = W * Y; var wz = W * Z;
            return new(
                new(1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)),
                new(2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)),
                new(2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)));
        }

        public static Rotation FromEulerDegrees(float pitch, float yaw, float roll)
        {
            const float degreesToRadians = MathF.PI / 180f;
            pitch *= degreesToRadians;
            yaw *= degreesToRadians;
            roll *= degreesToRadians;
            var pitchRotation = new Rotation(MathF.Cos(pitch / -2), MathF.Sin(pitch / -2), 0, 0);
            var yawRotation = new Rotation(MathF.Cos(yaw / 2), 0, MathF.Sin(yaw / 2), 0);
            var rollRotation = new Rotation(MathF.Cos(roll / -2), 0, 0, MathF.Sin(roll / -2));
            return (yawRotation * pitchRotation * rollRotation).Normalized();
        }

        public static Rotation FromMatrix(Matrix3 source)
        {
            var k = Normalize(source.K);
            var i = Normalize(Cross(source.J, k));
            var j = Cross(k, i);
            var m = new Matrix3(i, j, k);
            var trace = m.I.X + m.J.Y + m.K.Z;
            float w, x, y, z, t;
            if (trace > 0)
            {
                t = MathF.Sqrt(trace + 1) * 2;
                x = (m.K.Y - m.J.Z) / t;
                y = (m.I.Z - m.K.X) / t;
                z = (m.J.X - m.I.Y) / t;
                w = t / 4;
            }
            else if (m.I.X > m.J.Y && m.I.X > m.K.Z)
            {
                t = MathF.Sqrt(m.I.X - m.J.Y - m.K.Z + 1) * 2;
                x = t / 4;
                y = (m.J.X + m.I.Y) / t;
                z = (m.I.Z + m.K.X) / t;
                w = (m.K.Y - m.J.Z) / t;
            }
            else if (m.J.Y > m.K.Z)
            {
                t = MathF.Sqrt(m.J.Y - m.K.Z - m.I.X + 1) * 2;
                x = (m.J.X + m.I.Y) / t;
                y = t / 4;
                z = (m.K.Y + m.J.Z) / t;
                w = (m.I.Z - m.K.X) / t;
            }
            else
            {
                t = MathF.Sqrt(m.K.Z - m.J.Y - m.I.X + 1) * 2;
                x = (m.I.Z + m.K.X) / t;
                y = (m.K.Y + m.J.Z) / t;
                z = t / 4;
                w = (m.J.X - m.I.Y) / t;
            }
            return new Rotation(w, x, y, z);
        }

        public float PitchDegrees => -MathF.Atan2(ToMatrix().K.Y, MathF.Sqrt(ToMatrix().K.X * ToMatrix().K.X + ToMatrix().K.Z * ToMatrix().K.Z)) * (180f / MathF.PI);
        public float YawDegrees => -MathF.Atan2(ToMatrix().K.X, ToMatrix().K.Z) * (180f / MathF.PI);
        public float RollDegrees
        {
            get
            {
                var matrix = ToMatrix();
                return MathF.Atan2(matrix.I.Y, matrix.J.Y) * (180f / MathF.PI);
            }
        }
    }

    private static SceneVector3 Add(SceneVector3 a, SceneVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static SceneVector3 Subtract(SceneVector3 a, SceneVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static SceneVector3 Multiply(SceneVector3 value, float scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    private static SceneVector3 Multiply(SceneVector3 a, SceneVector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    private static SceneVector3 Divide(SceneVector3 a, SceneVector3 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
    private static float Dot(SceneVector3 a, SceneVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static SceneVector3 Cross(SceneVector3 a, SceneVector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static float Length(SceneVector3 value) => MathF.Sqrt(Dot(value, value));
    private static SceneVector3 Normalize(SceneVector3 value) => Multiply(value, 1 / Length(value));
}
