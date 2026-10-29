using System.Numerics;

namespace Blitz3DCS.Engine;

/// <summary>A reusable, preorder-indexed snapshot of a B3D model's evaluated local pose and model transforms.</summary>
public sealed class B3dPose
{
    private readonly B3dModel _model;
    private readonly B3dNode[] _sourceNodes;
    private readonly int[] _parentIndices;
    private readonly Tracks[] _tracks;
    private readonly B3dPoseNode[] _nodes;

    /// <summary>Creates a pose buffer and builds its stable preorder node index once.</summary>
    public B3dPose(B3dModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        var sourceNodes = new List<B3dNode>();
        var parents = new List<int>();
        AddPreorder(model.Root, -1, sourceNodes, parents);
        _sourceNodes = sourceNodes.ToArray();
        _parentIndices = parents.ToArray();
        _tracks = _sourceNodes.Select(node => Tracks.Create(node.Keys)).ToArray();
        _nodes = new B3dPoseNode[_sourceNodes.Length];
    }

    /// <summary>Number of nodes in stable root-first, child-insertion-order traversal.</summary>
    public int Count => _nodes.Length;

    /// <summary>Evaluated nodes, indexed in stable preorder order.</summary>
    public ReadOnlySpan<B3dPoseNode> Nodes => _nodes;

    internal B3dModel Model => _model;
    internal B3dNode[] SourceNodes => _sourceNodes;
    internal int[] ParentIndices => _parentIndices;
    internal Tracks[] AnimationTracks => _tracks;
    internal B3dPoseNode[] MutableNodes => _nodes;
    internal bool IsEvaluated { get; set; }

    private static void AddPreorder(B3dNode node, int parentIndex, List<B3dNode> nodes, List<int> parents)
    {
        var index = nodes.Count;
        nodes.Add(node);
        parents.Add(parentIndex);
        foreach (var child in node.Children) AddPreorder(child, index, nodes, parents);
    }

    internal sealed class Tracks
    {
        public B3dAnimationKey[] Position { get; }
        public B3dAnimationKey[] Scale { get; }
        public B3dAnimationKey[] Rotation { get; }

        private Tracks(B3dAnimationKey[] position, B3dAnimationKey[] scale, B3dAnimationKey[] rotation)
        {
            Position = position;
            Scale = scale;
            Rotation = rotation;
        }

        public static Tracks Create(IEnumerable<B3dAnimationKey> keys)
        {
            var position = new SortedDictionary<int, B3dAnimationKey>();
            var scale = new SortedDictionary<int, B3dAnimationKey>();
            var rotation = new SortedDictionary<int, B3dAnimationKey>();
            foreach (var key in keys)
            {
                if (key.Position.HasValue) position[key.Frame] = key;
                if (key.Scale.HasValue) scale[key.Frame] = key;
                if (key.Rotation.HasValue) rotation[key.Frame] = key;
            }
            return new Tracks(position.Values.ToArray(), scale.Values.ToArray(), rotation.Values.ToArray());
        }
    }
}

/// <summary>One evaluated node in a reusable <see cref="B3dPose"/> buffer.</summary>
public readonly record struct B3dPoseNode(
    B3dNode Source,
    int ParentIndex,
    B3dVector3 LocalPosition,
    B3dVector3 LocalScale,
    B3dQuaternion LocalRotation,
    Matrix4x4 LocalMatrix,
    Matrix4x4 ModelMatrix,
    bool HasPositionKeys,
    bool HasScaleKeys,
    bool HasRotationKeys);

/// <summary>Samples B3D local animation tracks and composes their model-space transforms.</summary>
public static class B3dAnimationEvaluator
{
    /// <summary>
    /// Evaluates one frame into a pose buffer created for the same model. Frames wrap over the B3D
    /// animation length by default; callers can disable wrapping and clip keys to an extracted range.
    /// Unkeyed channels keep the node's original local value.
    /// </summary>
    public static void EvaluateInto(B3dModel model, float frame, B3dPose pose, bool wrapFrames = true,
        int? rangeStartFrame = null, int? rangeEndFrame = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pose);
        if (!ReferenceEquals(model, pose.Model)) throw new ArgumentException("The pose buffer belongs to a different B3D model.", nameof(pose));
        if (!float.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame), "Animation frame must be finite.");
        if (rangeStartFrame.HasValue != rangeEndFrame.HasValue)
            throw new ArgumentException("Both extracted-range endpoints must be supplied.");
        if (rangeStartFrame > rangeEndFrame)
            throw new ArgumentOutOfRangeException(nameof(rangeStartFrame), "The extracted animation range is reversed.");

        var frameCount = (model.Animation ?? model.FileAnimation)?.FrameCount ?? 0;
        if (wrapFrames && frameCount > 0)
        {
            frame %= frameCount;
            if (frame < 0) frame += frameCount;
        }

        var sourceNodes = pose.SourceNodes;
        var parentIndices = pose.ParentIndices;
        var tracks = pose.AnimationTracks;
        var destination = pose.MutableNodes;
        for (var index = 0; index < destination.Length; index++)
        {
            var source = sourceNodes[index];
            var nodeTracks = tracks[index];
            var hasPositionKeys = HasKeysInRange(nodeTracks.Position, rangeStartFrame, rangeEndFrame);
            var hasScaleKeys = HasKeysInRange(nodeTracks.Scale, rangeStartFrame, rangeEndFrame);
            var hasRotationKeys = HasKeysInRange(nodeTracks.Rotation, rangeStartFrame, rangeEndFrame);
            var position = SampleVector(nodeTracks.Position, frame, source.Position, positionChannel: true, rangeStartFrame, rangeEndFrame);
            var scale = SampleVector(nodeTracks.Scale, frame, source.Scale, positionChannel: false, rangeStartFrame, rangeEndFrame);
            var rotation = SampleRotation(nodeTracks.Rotation, frame, source.Rotation, rangeStartFrame, rangeEndFrame);
            var local = Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z)
                        * Matrix4x4.CreateFromQuaternion(ToNumerics(rotation))
                        * Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
            var parentIndex = parentIndices[index];
            var modelMatrix = parentIndex < 0 ? local : local * destination[parentIndex].ModelMatrix;
            destination[index] = new B3dPoseNode(source, parentIndex, position, scale, rotation, local, modelMatrix,
                hasPositionKeys, hasScaleKeys, hasRotationKeys);
        }
        pose.IsEvaluated = true;
    }

    private static B3dVector3 SampleVector(B3dAnimationKey[] keys, float frame, B3dVector3 fallback, bool positionChannel,
        int? rangeStartFrame, int? rangeEndFrame)
    {
        var pair = FindPair(keys, frame, rangeStartFrame, rangeEndFrame);
        if (pair.Current is null) return fallback;
        var from = positionChannel ? pair.Current.Position!.Value : pair.Current.Scale!.Value;
        if (pair.Next is null) return from;
        var to = positionChannel ? pair.Next.Position!.Value : pair.Next.Scale!.Value;
        var amount = (frame - pair.Current.Frame) / (pair.Next.Frame - pair.Current.Frame);
        return new B3dVector3(
            from.X + (to.X - from.X) * amount,
            from.Y + (to.Y - from.Y) * amount,
            from.Z + (to.Z - from.Z) * amount);
    }

    private static B3dQuaternion SampleRotation(B3dAnimationKey[] keys, float frame, B3dQuaternion fallback,
        int? rangeStartFrame, int? rangeEndFrame)
    {
        var pair = FindPair(keys, frame, rangeStartFrame, rangeEndFrame);
        if (pair.Current is null) return fallback;
        var from = pair.Current.Rotation!.Value;
        if (pair.Next is null) return from;
        var to = pair.Next.Rotation!.Value;
        var amount = (frame - pair.Current.Frame) / (pair.Next.Frame - pair.Current.Frame);
        return Slerp(from, to, amount);
    }

    private static (B3dAnimationKey? Current, B3dAnimationKey? Next) FindPair(B3dAnimationKey[] keys, float frame,
        int? rangeStartFrame, int? rangeEndFrame)
    {
        if (keys.Length == 0) return (null, null);

        var start = 0;
        var end = keys.Length - 1;
        if (rangeStartFrame.HasValue)
        {
            start = LowerBound(keys, rangeStartFrame.Value);
            end = UpperBound(keys, rangeEndFrame!.Value) - 1;
            if (start > end) return (null, null);
        }
        if (start == end || frame < keys[start].Frame) return (keys[start], null);

        var wholeFrame = frame >= int.MaxValue ? int.MaxValue : (int)frame;
        var low = start;
        var high = end + 1;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (keys[middle].Frame <= wholeFrame) low = middle + 1;
            else high = middle;
        }

        var currentIndex = low - 1;
        if (low == end + 1) return (keys[currentIndex], null);
        return (keys[currentIndex], keys[low]);
    }

    private static bool HasKeysInRange(B3dAnimationKey[] keys, int? rangeStartFrame, int? rangeEndFrame)
    {
        if (keys.Length == 0) return false;
        if (!rangeStartFrame.HasValue) return true;
        var first = LowerBound(keys, rangeStartFrame.Value);
        return first < keys.Length && keys[first].Frame <= rangeEndFrame!.Value;
    }

    private static int LowerBound(B3dAnimationKey[] keys, int frame)
    {
        var low = 0;
        var high = keys.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (keys[middle].Frame < frame) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(B3dAnimationKey[] keys, int frame)
    {
        var low = 0;
        var high = keys.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (keys[middle].Frame <= frame) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static B3dQuaternion Slerp(B3dQuaternion from, B3dQuaternion to, float amount)
    {
        var dot = from.W * to.W + from.X * to.X + from.Y * to.Y + from.Z * to.Z;
        if (dot < 0)
        {
            to = new B3dQuaternion(-to.W, -to.X, -to.Y, -to.Z);
            dot = -dot;
        }

        var fromWeight = 1 - amount;
        var toWeight = amount;
        if (dot < 1 - 1e-6f)
        {
            var angle = MathF.Acos(Math.Clamp(dot, -1, 1));
            var sine = MathF.Sin(angle);
            fromWeight = MathF.Sin((1 - amount) * angle) / sine;
            toWeight = MathF.Sin(amount * angle) / sine;
        }

        return new B3dQuaternion(
            from.W * fromWeight + to.W * toWeight,
            from.X * fromWeight + to.X * toWeight,
            from.Y * fromWeight + to.Y * toWeight,
            from.Z * fromWeight + to.Z * toWeight);
    }

    private static Quaternion ToNumerics(B3dQuaternion value) => new(value.X, value.Y, value.Z, value.W);
}
