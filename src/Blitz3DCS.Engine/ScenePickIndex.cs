namespace Blitz3DCS.Engine;

/// <summary>
/// Pick-target bounds index used by LinePick/EntityPick/EntityVisible (EngineOptimizations.PickAccel).
/// Every entity with a nonzero pick mode owns a <see cref="PickEntry"/> holding a conservative world-space AABB of
/// everything its narrow-phase test can report (sphere: centre +- radius, box: centre +- sum of the extents along the
/// unit basis used by TryBox, polygon: the transformed local triangle bounds), padded by a float-error margin.
/// Entries live in a static BVH (rebuilt lazily) or, once they move/are added, in a short dynamic list. A query visits
/// only entries whose padded AABB the segment touches, re-validates them against the live scene (enabled chain, pick
/// mode, obscurer) and runs the unchanged TryHit narrow phase in the original enumeration order, so the nearest-hit
/// selection, tie-breaking and the picked outputs are those of the full scan.
/// Invalidation: an entry is marked dirty when its node's world transform is invalidated (clean-to-dirty transition,
/// which covers ancestors moving), when its pick mode or shape (radius, box, triangles, plane) changes, and it is
/// dropped when its collision state is forgotten or replaced. All per-query scratch is local or picking-only.
/// Known limit of the conservative argument: TrySphere (also used for box/triangle vertices when radius > 0) accepts a
/// slightly negative time, so for a ray exactly tangent to a sphere that starts beyond the tangent point and moves
/// away, the full scan can report a "hit" behind the segment start; the index rejects such a target.
/// </summary>
public sealed partial class SceneGraph
{
    private const int PickLeafSize = 4;
    private readonly List<PickEntry> _pickEntries = [];
    private readonly List<PickEntry> _pickDirty = [];
    private readonly List<PickEntry> _pickDynamic = [];
    private readonly List<PickEntry> _pickCandidates = [];
    private PickEntry[] _pickStatic = [];
    private PickBvhNode[] _pickBvh = [];
    private int _pickBvhCount;
    private int _pickChurn;
    private bool _legacyPickPath;

    /// <summary>Testing hook: run picking queries on the original full scan (same as BLITZCS_ENGINE_NO=pickaccel).</summary>
    internal bool UseLegacyPickPathForTesting { get => _legacyPickPath; set => _legacyPickPath = value; }

    /// <summary>Testing/diagnostics: number of static pick BVH rebuilds.</summary>
    internal int PickIndexRebuildCountForTesting { get; private set; }

    /// <summary>Testing/diagnostics: number of narrow-phase target tests run by accelerated pick queries.</summary>
    internal long PickNarrowTestsForTesting { get; private set; }

    private bool PickAccelActive => EngineOptimizations.PickAccel && !_legacyPickPath;

    private sealed class PickEntry(Node node, CollisionEntityState state, List<PickEntry> dirtyList)
    {
        public readonly Node Node = node;
        public readonly CollisionEntityState State = state;
        private readonly List<PickEntry> _dirtyList = dirtyList;
        public int Index;
        public bool Dirty, Removed, InStatic, InDynamic, Always;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public float SMinX, SMinY, SMinZ, SMaxX, SMaxY, SMaxZ;
        public int EnabledVersion = int.MinValue;
        public bool Enabled;
        public int KeyVersion = int.MinValue;
        public int[] Key = [];
        public int KeyLength;

        public void MarkDirty()
        {
            if (Dirty || Removed) return;
            Dirty = true;
            _dirtyList.Add(this);
        }
    }

    private struct PickBvhNode
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int Left, Right; // children (internal) or -1
        public int Start, Count; // leaf range in _pickStatic
    }

    /// <summary>Registers, refreshes or drops the pick entry of <paramref name="state"/> after its pick mode changed.</summary>
    private void PickTrack(Node node, CollisionEntityState state)
    {
        if (!EngineOptimizations.PickAccel) return;
        if (state.PickMode == 0)
        {
            PickUntrack(state);
            return;
        }
        if (state.PickEntry is { } existing) { existing.MarkDirty(); return; }
        var entry = new PickEntry(node, state, _pickDirty) { Index = _pickEntries.Count };
        _pickEntries.Add(entry);
        state.PickEntry = entry;
        entry.MarkDirty();
    }

    private void PickUntrack(CollisionEntityState? state)
    {
        if (state?.PickEntry is not { } entry) return;
        state.PickEntry = null;
        entry.Removed = true;
        var last = _pickEntries.Count - 1;
        var moved = _pickEntries[last];
        _pickEntries[entry.Index] = moved;
        moved.Index = entry.Index;
        _pickEntries.RemoveAt(last);
        _pickChurn++;
    }

    /// <summary>A shape parameter (radius, box, triangles, plane flag) of a possibly pickable state changed.</summary>
    private static void PickShapeChanged(CollisionEntityState state) => state.PickEntry?.MarkDirty();

    private void RefreshPickIndex()
    {
        if (_pickDirty.Count != 0)
        {
            var dirty = _pickDirty;
            for (var i = 0; i < dirty.Count; i++)
            {
                var entry = dirty[i];
                if (entry.Removed) continue;
                entry.Dirty = false;
                ComputePickBounds(entry);
                if (entry.InStatic)
                {
                    if (!entry.Always && entry.MinX == entry.SMinX && entry.MinY == entry.SMinY && entry.MinZ == entry.SMinZ
                        && entry.MaxX == entry.SMaxX && entry.MaxY == entry.SMaxY && entry.MaxZ == entry.SMaxZ) continue;
                    entry.InStatic = false;
                    _pickChurn++;
                }
                if (!entry.InDynamic)
                {
                    entry.InDynamic = true;
                    _pickDynamic.Add(entry);
                    if (!entry.Always) _pickChurn++;
                }
            }
            dirty.Clear();
        }
        if (_pickChurn > Math.Max(64, _pickEntries.Count / 4)) RebuildPickBvh();
    }

    private void ComputePickBounds(PickEntry entry)
    {
        var state = entry.State;
        var world = GetWorldTransform(entry.Node);
        var pos = world.Position;
        float minX, minY, minZ, maxX, maxY, maxZ;
        entry.Always = false;
        switch (state.PickMode)
        {
            case CollisionMethodSphere:
            {
                var r = MathF.Abs(state.Radii.X);
                minX = pos.X - r; minY = pos.Y - r; minZ = pos.Z - r;
                maxX = pos.X + r; maxY = pos.Y + r; maxZ = pos.Z + r;
                break;
            }
            case CollisionMethodBox:
            {
                // TryBox works in the frame of the normalized basis; with unit columns |M v|_k <= sum_j |v_j|. An
                // ill-conditioned frame amplifies rounding in its inverse, so such targets are always tested.
                var i = Normalize(world.BasisX); var j = Normalize(world.BasisY); var k = Normalize(world.BasisZ);
                var det = Dot(i, Cross(j, k));
                if (!(MathF.Abs(det) >= 0.1f)) { entry.Always = true; return; }
                var min = state.BoxMinimum; var max = state.BoxMaximum;
                var e = MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)) + MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y))
                    + MathF.Max(MathF.Abs(min.Z), MathF.Abs(max.Z));
                minX = pos.X - e; minY = pos.Y - e; minZ = pos.Z - e;
                maxX = pos.X + e; maxY = pos.Y + e; maxZ = pos.Z + e;
                break;
            }
            case CollisionMethodPolygon when !state.InfinitePlane:
            {
                var collider = state.TriangleCollider ??= new SceneTriangleCollider(state.Triangles);
                if (!collider.TryGetLocalBounds(out var local)) { entry.Always = true; return; }
                minX = minY = minZ = float.PositiveInfinity;
                maxX = maxY = maxZ = float.NegativeInfinity;
                for (var corner = 0; corner < 8; corner++)
                {
                    var p = world.TransformPoint(new((corner & 1) == 0 ? local.Minimum.X : local.Maximum.X,
                        (corner & 2) == 0 ? local.Minimum.Y : local.Maximum.Y, (corner & 4) == 0 ? local.Minimum.Z : local.Maximum.Z));
                    minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); minZ = MathF.Min(minZ, p.Z);
                    maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y); maxZ = MathF.Max(maxZ, p.Z);
                }
                break;
            }
            default:
                // Infinite planes and unknown modes: always handed to the narrow phase.
                entry.Always = true;
                return;
        }

        var magnitude = MathF.Max(MathF.Max(MathF.Max(MathF.Abs(minX), MathF.Abs(maxX)), MathF.Max(MathF.Abs(minY), MathF.Abs(maxY))),
            MathF.Max(MathF.Abs(minZ), MathF.Abs(maxZ)));
        var extent = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
        var margin = PickMargin(magnitude, extent);
        minX -= margin; minY -= margin; minZ -= margin;
        maxX += margin; maxY += margin; maxZ += margin;
        if (!float.IsFinite(minX + minY + minZ + maxX + maxY + maxZ) || !float.IsFinite(margin))
        {
            entry.Always = true;
            return;
        }
        entry.MinX = minX; entry.MinY = minY; entry.MinZ = minZ;
        entry.MaxX = maxX; entry.MaxY = maxY; entry.MaxZ = maxZ;
    }

    /// <summary>
    /// Float-error allowance: absolute 0.01, 1e-4 of the coordinate magnitude (rounding of positions/transforms) and
    /// 2e-3 of the size involved (cancellation in the quadratic/plane solves grows with the start-to-target distance,
    /// which for any candidate is bounded by the segment length plus the target extent).
    /// </summary>
    private static float PickMargin(float magnitude, float size) => 0.01f + 1e-4f * magnitude + 2e-3f * size;

    private void RebuildPickBvh()
    {
        PickIndexRebuildCountForTesting++;
        _pickChurn = 0;
        _pickDynamic.Clear();
        var count = 0;
        if (_pickStatic.Length < _pickEntries.Count) _pickStatic = new PickEntry[_pickEntries.Count];
        foreach (var entry in _pickEntries)
        {
            if (entry.Always)
            {
                entry.InStatic = false;
                entry.InDynamic = true;
                _pickDynamic.Add(entry);
                continue;
            }
            entry.InDynamic = false;
            entry.InStatic = true;
            entry.SMinX = entry.MinX; entry.SMinY = entry.MinY; entry.SMinZ = entry.MinZ;
            entry.SMaxX = entry.MaxX; entry.SMaxY = entry.MaxY; entry.SMaxZ = entry.MaxZ;
            _pickStatic[count++] = entry;
        }
        Array.Clear(_pickStatic, count, _pickStatic.Length - count);
        _pickBvhCount = 0;
        if (count == 0) return;
        if (_pickBvh.Length < 2 * count) _pickBvh = new PickBvhNode[2 * count];
        var keys = new float[count];
        BuildPickNode(0, count, keys);
    }

    private int BuildPickNode(int start, int count, float[] keys)
    {
        var index = _pickBvhCount++;
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        float cMinX = float.PositiveInfinity, cMinY = float.PositiveInfinity, cMinZ = float.PositiveInfinity;
        float cMaxX = float.NegativeInfinity, cMaxY = float.NegativeInfinity, cMaxZ = float.NegativeInfinity;
        for (var i = start; i < start + count; i++)
        {
            var e = _pickStatic[i];
            minX = MathF.Min(minX, e.MinX); minY = MathF.Min(minY, e.MinY); minZ = MathF.Min(minZ, e.MinZ);
            maxX = MathF.Max(maxX, e.MaxX); maxY = MathF.Max(maxY, e.MaxY); maxZ = MathF.Max(maxZ, e.MaxZ);
            var cx = (e.MinX + e.MaxX) * .5f; var cy = (e.MinY + e.MaxY) * .5f; var cz = (e.MinZ + e.MaxZ) * .5f;
            cMinX = MathF.Min(cMinX, cx); cMinY = MathF.Min(cMinY, cy); cMinZ = MathF.Min(cMinZ, cz);
            cMaxX = MathF.Max(cMaxX, cx); cMaxY = MathF.Max(cMaxY, cy); cMaxZ = MathF.Max(cMaxZ, cz);
        }
        ref var node = ref _pickBvh[index];
        node.MinX = minX; node.MinY = minY; node.MinZ = minZ;
        node.MaxX = maxX; node.MaxY = maxY; node.MaxZ = maxZ;
        node.Left = node.Right = -1;
        node.Start = start; node.Count = count;
        if (count <= PickLeafSize) return index;

        var dx = cMaxX - cMinX; var dy = cMaxY - cMinY; var dz = cMaxZ - cMinZ;
        var axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;
        for (var i = 0; i < count; i++)
        {
            var e = _pickStatic[start + i];
            keys[start + i] = axis == 0 ? e.MinX + e.MaxX : axis == 1 ? e.MinY + e.MaxY : e.MinZ + e.MaxZ;
        }
        Array.Sort(keys, _pickStatic, start, count);
        var half = count / 2;
        var left = BuildPickNode(start, half, keys);
        var right = BuildPickNode(start + half, count - half, keys);
        ref var parent = ref _pickBvh[index];
        parent.Left = left;
        parent.Right = right;
        parent.Count = 0;
        return index;
    }

    /// <summary>Collects pick entries whose padded bounds the segment start + t*direction, t in [0,1], can touch.</summary>
    private void QueryPickIndex(SceneVector3 start, SceneVector3 direction, float radius, List<PickEntry> result)
    {
        result.Clear();
        var end = Add(start, direction);
        var magnitude = MathF.Max(MathF.Max(MathF.Max(MathF.Abs(start.X), MathF.Abs(end.X)), MathF.Max(MathF.Abs(start.Y), MathF.Abs(end.Y))),
            MathF.Max(MathF.Abs(start.Z), MathF.Abs(end.Z)));
        var length = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
        // Box hits are measured in the target's unit frame: the radius can spread over all three axes.
        var pad = (double)(3 * MathF.Abs(radius) + PickMargin(magnitude, length));
        double sx = start.X, sy = start.Y, sz = start.Z, dx = direction.X, dy = direction.Y, dz = direction.Z;

        if (_pickBvhCount != 0)
        {
            Span<int> stack = stackalloc int[128];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                ref var node = ref _pickBvh[stack[--top]];
                if (!SegmentTouches(sx, sy, sz, dx, dy, dz, node.MinX - pad, node.MinY - pad, node.MinZ - pad,
                    node.MaxX + pad, node.MaxY + pad, node.MaxZ + pad)) continue;
                if (node.Left < 0)
                {
                    for (var i = node.Start; i < node.Start + node.Count; i++)
                    {
                        var e = _pickStatic[i];
                        if (!e.InStatic || e.Removed) continue;
                        if (SegmentTouches(sx, sy, sz, dx, dy, dz, e.MinX - pad, e.MinY - pad, e.MinZ - pad,
                            e.MaxX + pad, e.MaxY + pad, e.MaxZ + pad)) result.Add(e);
                    }
                    continue;
                }
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
        }

        var dynamic = _pickDynamic;
        for (var i = 0; i < dynamic.Count; i++)
        {
            var e = dynamic[i];
            if (e.Removed || e.InStatic) continue;
            if (e.Always || SegmentTouches(sx, sy, sz, dx, dy, dz, e.MinX - pad, e.MinY - pad, e.MinZ - pad,
                e.MaxX + pad, e.MaxY + pad, e.MaxZ + pad)) result.Add(e);
        }
    }

    /// <summary>Slab test of the closed segment against a closed box, in double precision; NaN bounds never reject.</summary>
    private static bool SegmentTouches(double sx, double sy, double sz, double dx, double dy, double dz,
        double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        double t0 = 0, t1 = 1;
        return Slab(sx, dx, minX, maxX, ref t0, ref t1) && Slab(sy, dy, minY, maxY, ref t0, ref t1) && Slab(sz, dz, minZ, maxZ, ref t0, ref t1);

        static bool Slab(double s, double d, double min, double max, ref double t0, ref double t1)
        {
            if (d == 0) return !(s < min || s > max);
            var inv = 1 / d;
            var ta = (min - s) * inv;
            var tb = (max - s) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            if (ta > t0) t0 = ta;
            if (tb < t1) t1 = tb;
            return !(t0 > t1);
        }
    }

    /// <summary>Whether the entry's node and every ancestor are enabled (the original CollectEnabled membership).</summary>
    private bool PickEnabled(PickEntry entry)
    {
        if (entry.EnabledVersion == _structureVersion) return entry.Enabled;
        var enabled = true;
        for (var node = entry.Node; node is not null; node = node.Parent)
            if (!node.Enabled) { enabled = false; break; }
        entry.Enabled = enabled;
        entry.EnabledVersion = _structureVersion;
        return enabled;
    }

    /// <summary>Depth-first position key: root slot (the `_nodes.Values` root order), then child indices.</summary>
    private int[] PickKey(PickEntry entry, out int length)
    {
        if (entry.KeyVersion != _structureVersion)
        {
            var depth = 0;
            for (var node = entry.Node; node is not null; node = node.Parent) depth++;
            if (entry.Key.Length < depth) entry.Key = new int[depth];
            var key = entry.Key;
            var i = depth - 1;
            for (var node = entry.Node; node is not null; node = node.Parent, i--)
                key[i] = node.Parent is null ? node.Slot : node.Parent.Children.IndexOf(node);
            entry.KeyLength = depth;
            entry.KeyVersion = _structureVersion;
        }
        length = entry.KeyLength;
        return entry.Key;
    }

    private int ComparePickOrder(PickEntry left, PickEntry right)
    {
        var a = PickKey(left, out var la);
        var b = PickKey(right, out var lb);
        var n = Math.Min(la, lb);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        return la.CompareTo(lb);
    }

    private void SortPickCandidates(List<PickEntry> list)
    {
        for (var i = 1; i < list.Count; i++)
        {
            var item = list[i];
            var j = i - 1;
            while (j >= 0 && ComparePickOrder(list[j], item) > 0)
            {
                list[j + 1] = list[j];
                j--;
            }
            list[j + 1] = item;
        }
    }

    private static bool IsFinite(SceneVector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private int LinePickAccelerated(SceneVector3 start, SceneVector3 direction, float radius)
    {
        RefreshPickIndex();
        var candidates = _pickCandidates;
        QueryPickIndex(start, direction, radius, candidates);
        var write = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e.State.PickMode != 0 && PickEnabled(e)) candidates[write++] = e;
        }
        candidates.RemoveRange(write, candidates.Count - write);
        if (candidates.Count > 1) SortPickCandidates(candidates);

        var bestTime = 1f;
        var bestEntity = 0;
        var bestNormal = _pickedNormal;
        var bestPosition = _pickedPosition;
        foreach (var entry in candidates)
        {
            var target = entry.Node;
            var state = entry.State;
            PickNarrowTestsForTesting++;
            var rule = new CollisionRule(0, 0, state.PickMode, CollisionResponseNone);
            if (!TryHit(rule, target, state, start, direction, radius, 1, bestTime, false, out var hit)) continue;
            bestTime = hit.Time;
            bestEntity = target.Id;
            bestNormal = hit.Normal;
            bestPosition = Subtract(Add(start, Multiply(direction, hit.Time)), Multiply(hit.Normal, radius));
        }
        candidates.Clear();

        _pickedEntity = bestEntity;
        if (bestEntity != 0)
        {
            _pickedNormal = bestNormal;
            _pickedPosition = bestPosition;
        }
        return bestEntity;
    }

    private bool EntityVisibleAccelerated(int source, int destination, SceneVector3 start, SceneVector3 direction)
    {
        RefreshPickIndex();
        var candidates = _pickCandidates;
        QueryPickIndex(start, direction, 0, candidates);
        var visible = true;
        foreach (var entry in candidates)
        {
            var target = entry.Node;
            if (target.Id == source || target.Id == destination) continue;
            var state = entry.State;
            if (state.PickMode == 0 || !state.PickObscurer || !PickEnabled(entry)) continue;
            PickNarrowTestsForTesting++;
            var rule = new CollisionRule(0, 0, state.PickMode, CollisionResponseNone);
            if (TryHit(rule, target, state, start, direction, 0, 1, 1, false, out _)) { visible = false; break; }
        }
        candidates.Clear();
        return visible;
    }
}
