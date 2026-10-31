namespace Blitz3DCS.Engine;

/// <summary>Local-space triangle bounds and BVH used to reject polygon sweep candidates.</summary>
internal sealed class SceneTriangleCollider
{
    private const int LeafTriangleCount = 16;
    private readonly SceneCollisionTriangle[] _triangles;
    private readonly TriangleBounds[] _triangleBounds;
    private readonly SceneVector3[] _triangleCentres;
    private readonly int[] _triangleOrder;
    private readonly CentroidComparer _centroidComparer;
    private readonly Node? _root;

    internal SceneTriangleCollider(SceneCollisionTriangle[] triangles)
    {
        _triangles = triangles;
        _triangleBounds = new TriangleBounds[triangles.Length];
        _triangleCentres = new SceneVector3[triangles.Length];
        _triangleOrder = new int[triangles.Length];
        for (var index = 0; index < triangles.Length; index++)
        {
            var triangle = triangles[index];
            var bounds = TriangleBounds.From(triangle);
            _triangleBounds[index] = bounds;
            _triangleCentres[index] = new(
                (bounds.Minimum.X + bounds.Maximum.X) * .5f,
                (bounds.Minimum.Y + bounds.Maximum.Y) * .5f,
                (bounds.Minimum.Z + bounds.Maximum.Z) * .5f);
            _triangleOrder[index] = index;
        }
        _centroidComparer = new CentroidComparer(_triangleCentres);
        _root = triangles.Length == 0 ? null : BuildNode(0, triangles.Length);
    }

    internal int TriangleCount => _triangles.Length;

    /// <summary>Local-space bounds of every triangle; false when there is no geometry.</summary>
    internal bool TryGetLocalBounds(out TriangleBounds bounds)
    {
        bounds = _root is null ? default : _root.Bounds;
        return _root is not null;
    }

    /// <summary>
    /// Returns original triangle ordinals whose cached local bounds overlap a conservative
    /// local-space AABB of the swept sphere. Result ordering matches the source triangle list.
    /// </summary>
    internal void Query(SceneTransform targetTransform, SceneVector3 lineStart, SceneVector3 lineDirection,
        float radius, List<int> candidates)
    {
        candidates.Clear();
        if (_root is null) return;

        if (!TryGetLocalSweepBounds(targetTransform, lineStart, lineDirection, radius, out var queryBounds))
        {
            for (var index = 0; index < _triangles.Length; index++) candidates.Add(index);
            return;
        }

        QueryNode(_root, queryBounds, candidates);
        candidates.Sort();
    }

    internal static bool TryGetLocalSweepBounds(SceneTransform targetTransform, SceneVector3 lineStart,
        SceneVector3 lineDirection, float radius, out TriangleBounds bounds)
    {
        bounds = default;
        var crossYZ = Cross(targetTransform.BasisY, targetTransform.BasisZ);
        var determinant = Dot(targetTransform.BasisX, crossYZ);
        if (!float.IsFinite(determinant) || MathF.Abs(determinant) <= 1e-20f || !float.IsFinite(radius) || radius < 0)
            return false;

        var lineEnd = Add(lineStart, lineDirection);
        var worldMinimum = new SceneVector3(
            MathF.Min(lineStart.X, lineEnd.X) - radius,
            MathF.Min(lineStart.Y, lineEnd.Y) - radius,
            MathF.Min(lineStart.Z, lineEnd.Z) - radius);
        var worldMaximum = new SceneVector3(
            MathF.Max(lineStart.X, lineEnd.X) + radius,
            MathF.Max(lineStart.Y, lineEnd.Y) + radius,
            MathF.Max(lineStart.Z, lineEnd.Z) + radius);
        if (!IsFinite(worldMinimum) || !IsFinite(worldMaximum)) return false;

        var localMinimum = new SceneVector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var localMaximum = new SceneVector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (var corner = 0; corner < 8; corner++)
        {
            var worldPoint = new SceneVector3(
                (corner & 1) == 0 ? worldMinimum.X : worldMaximum.X,
                (corner & 2) == 0 ? worldMinimum.Y : worldMaximum.Y,
                (corner & 4) == 0 ? worldMinimum.Z : worldMaximum.Z);
            var localPoint = targetTransform.InverseTransformPoint(worldPoint);
            if (!IsFinite(localPoint)) return false;
            localMinimum = Min(localMinimum, localPoint);
            localMaximum = Max(localMaximum, localPoint);
        }

        bounds = new(localMinimum, localMaximum);
        return true;
    }

    private Node BuildNode(int start, int count)
    {
        var bounds = _triangleBounds[_triangleOrder[start]];
        for (var index = start + 1; index < start + count; index++)
            bounds = TriangleBounds.Union(bounds, _triangleBounds[_triangleOrder[index]]);
        if (count <= LeafTriangleCount) return new(bounds, start, count, null, null);

        var axis = LongestAxis(bounds);
        _centroidComparer.Axis = axis;
        Array.Sort(_triangleOrder, start, count, _centroidComparer);
        var leftCount = count / 2;
        return new(bounds, 0, 0, BuildNode(start, leftCount), BuildNode(start + leftCount, count - leftCount));
    }

    private void QueryNode(Node node, TriangleBounds queryBounds, List<int> candidates)
    {
        EngineStats.Add(EngineStats.ColBvhNodes);
        if (!node.Bounds.Overlaps(queryBounds)) return;
        if (node.Left is null)
        {
            for (var index = node.Start; index < node.Start + node.Count; index++)
            {
                var triangleIndex = _triangleOrder[index];
                if (_triangleBounds[triangleIndex].Overlaps(queryBounds)) candidates.Add(triangleIndex);
            }
            return;
        }

        QueryNode(node.Left, queryBounds, candidates);
        QueryNode(node.Right!, queryBounds, candidates);
    }

    private static int LongestAxis(TriangleBounds bounds)
    {
        var width = bounds.Maximum.X - bounds.Minimum.X;
        var height = bounds.Maximum.Y - bounds.Minimum.Y;
        var depth = bounds.Maximum.Z - bounds.Minimum.Z;
        var maximum = width;
        if (height > maximum) maximum = height;
        if (depth > maximum) maximum = depth;
        if (maximum == height) return 1;
        return maximum == depth ? 2 : 0;
    }

    private static SceneVector3 Add(SceneVector3 left, SceneVector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    private static SceneVector3 Min(SceneVector3 left, SceneVector3 right) =>
        new(MathF.Min(left.X, right.X), MathF.Min(left.Y, right.Y), MathF.Min(left.Z, right.Z));

    private static SceneVector3 Max(SceneVector3 left, SceneVector3 right) =>
        new(MathF.Max(left.X, right.X), MathF.Max(left.Y, right.Y), MathF.Max(left.Z, right.Z));

    private static SceneVector3 Cross(SceneVector3 left, SceneVector3 right) =>
        new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);

    private static float Dot(SceneVector3 left, SceneVector3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static bool IsFinite(SceneVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class Node(TriangleBounds bounds, int start, int count, Node? left, Node? right)
    {
        public TriangleBounds Bounds { get; } = bounds;
        public int Start { get; } = start;
        public int Count { get; } = count;
        public Node? Left { get; } = left;
        public Node? Right { get; } = right;
    }

    private sealed class CentroidComparer(SceneVector3[] centres) : IComparer<int>
    {
        public int Axis { get; set; }

        public int Compare(int left, int right)
        {
            var comparison = Component(centres[left], Axis).CompareTo(Component(centres[right], Axis));
            return comparison != 0 ? comparison : left.CompareTo(right);
        }

        private static float Component(SceneVector3 value, int axis) => axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };
    }

    internal readonly record struct TriangleBounds(SceneVector3 Minimum, SceneVector3 Maximum)
    {
        public bool Overlaps(TriangleBounds other) =>
            Maximum.X >= other.Minimum.X && Minimum.X <= other.Maximum.X &&
            Maximum.Y >= other.Minimum.Y && Minimum.Y <= other.Maximum.Y &&
            Maximum.Z >= other.Minimum.Z && Minimum.Z <= other.Maximum.Z;

        public static TriangleBounds From(SceneCollisionTriangle triangle) => new(
            Min(triangle.A, Min(triangle.B, triangle.C)), Max(triangle.A, Max(triangle.B, triangle.C)));

        public static TriangleBounds Union(TriangleBounds left, TriangleBounds right) => new(
            Min(left.Minimum, right.Minimum), Max(left.Maximum, right.Maximum));
    }
}
