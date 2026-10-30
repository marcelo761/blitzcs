using System.Numerics;

namespace Blitz3DCS.Engine;

/// <summary>Reusable buffers and bind data for skinning one B3D mesh into caller-owned arrays.</summary>
public sealed class B3dSkinningBuffer
{
    private readonly B3dModel _model;
    private readonly int _meshNodeIndex;
    private readonly BoneBinding[] _bones;
    private readonly VertexInfluence[][] _influences;
    private readonly Matrix4x4[] _skinMatrices;
    private readonly Matrix4x4[] _normalMatrices;
    private readonly Matrix4x4[] _poseMatrices;
    private bool _skinMatricesPrepared;
    private readonly bool _legacy = EngineOptimizations.Disabled;
    // Flattened per-vertex data for the fast path (built once; the source mesh is immutable after load).
    private readonly Vector3[] _srcPosition;
    private readonly Vector3[] _srcNormal;
    private readonly bool[] _hasNormal;
    private readonly int[] _influenceStart;
    private readonly int[] _influenceBone;
    private readonly float[] _influenceWeight;
    private readonly NormalBasis[] _normalBasis;

    /// <summary>Test hook: forces the original per-vertex implementation for this buffer.</summary>
    internal bool ForceLegacy { get; set; }

    /// <summary>The mesh node whose vertices this buffer skins.</summary>
    public B3dNode MeshNode { get; }

    /// <summary>Creates bind data once; reuse the buffer for every animated frame.</summary>
    public B3dSkinningBuffer(B3dPose pose, B3dNode meshNode)
    {
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(meshNode);
        if (meshNode.Mesh is null) throw new ArgumentException("The node has no B3D mesh.", nameof(meshNode));

        _model = pose.Model;
        MeshNode = meshNode;
        _meshNodeIndex = FindNodeIndex(pose, meshNode);
        var bindMatrices = BuildBindMatrices(pose);
        var boneNodes = new List<int> { _meshNodeIndex }; // Upstream bone index zero is the mesh itself.
        if (meshNode.IsBone)
            boneNodes.Add(_meshNodeIndex); // A BONE chunk can coexist with MESH on the same source node.
        var sourceNodes = pose.SourceNodes;
        for (var index = _meshNodeIndex + 1; index < pose.Count; index++)
        {
            if (IsDescendantInMeshScope(pose, index, _meshNodeIndex) && sourceNodes[index].IsBone)
                boneNodes.Add(index);
        }

        _bones = new BoneBinding[boneNodes.Count];
        for (var i = 0; i < boneNodes.Count; i++)
        {
            var nodeIndex = boneNodes[i];
            if (!Matrix4x4.Invert(bindMatrices[nodeIndex], out var inverseBind))
                throw new ArgumentException($"Bone node '{sourceNodes[nodeIndex].Name}' has a singular bind transform.", nameof(meshNode));
            // Upstream skins in world space. Convert the result back to current mesh-local space
            // because the D3D11 scene graph applies the mesh transform after this deformation.
            _bones[i] = new BoneBinding(nodeIndex, inverseBind);
        }

        _influences = BuildInfluences(meshNode.Mesh, boneNodes, pose);
        _skinMatrices = new Matrix4x4[_bones.Length];
        _normalMatrices = new Matrix4x4[_bones.Length];
        _poseMatrices = new Matrix4x4[pose.Count];
        _normalBasis = new NormalBasis[_bones.Length];

        var meshVertices = meshNode.Mesh.Vertices;
        var count = meshVertices.Count;
        _srcPosition = new Vector3[count];
        _srcNormal = new Vector3[count];
        _hasNormal = new bool[count];
        _influenceStart = new int[count + 1];
        var total = 0;
        for (var v = 0; v < count; v++) total += _influences[v].Length;
        _influenceBone = new int[total];
        _influenceWeight = new float[total];
        var cursor = 0;
        for (var v = 0; v < count; v++)
        {
            var vertex = meshVertices[v];
            _srcPosition[v] = ToVector3(vertex.Position);
            if (vertex.Normal is { } n) { _srcNormal[v] = ToVector3(n); _hasNormal[v] = true; }
            _influenceStart[v] = cursor;
            foreach (var influence in _influences[v])
            {
                _influenceBone[cursor] = influence.BoneIndex;
                _influenceWeight[cursor] = influence.Weight;
                cursor++;
            }
        }
        _influenceStart[count] = cursor;
    }

    /// <summary>Skins positions and optional normals without allocating per frame.</summary>
    public void DeformInto(B3dPose pose, Span<B3dVector3> positions, Span<B3dVector3> normals = default)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!ReferenceEquals(_model, pose.Model)) throw new ArgumentException("The pose belongs to a different B3D model.", nameof(pose));
        if (!pose.IsEvaluated) throw new InvalidOperationException("Evaluate the B3D pose before deforming its mesh.");
        var nodes = pose.Nodes;
        for (var i = 0; i < nodes.Length; i++) _poseMatrices[i] = nodes[i].ModelMatrix;
        DeformIntoCore(_poseMatrices, positions, normals);
    }

    /// <summary>
    /// Skins using caller-supplied scene-world matrices in the pose's stable preorder node order.
    /// This lets runtime entities retain their own unkeyed/root transforms while sharing source bind data.
    /// </summary>
    public void DeformInto(B3dPose pose, ReadOnlySpan<Matrix4x4> currentWorldMatrices,
        Span<B3dVector3> positions, Span<B3dVector3> normals = default)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!ReferenceEquals(_model, pose.Model)) throw new ArgumentException("The pose belongs to a different B3D model.", nameof(pose));
        if (currentWorldMatrices.Length != pose.Count)
            throw new ArgumentException("World matrix count must match the pose node count.", nameof(currentWorldMatrices));
        DeformIntoCore(currentWorldMatrices, positions, normals);
    }

    private void DeformIntoCore(ReadOnlySpan<Matrix4x4> currentWorldMatrices,
        Span<B3dVector3> positions, Span<B3dVector3> normals)
    {
        var vertices = MeshNode.Mesh!.Vertices;
        if (positions.Length != vertices.Count) throw new ArgumentException("Position output length must match the mesh vertex count.", nameof(positions));
        if (!normals.IsEmpty && normals.Length != vertices.Count) throw new ArgumentException("Normal output length must match the mesh vertex count.", nameof(normals));

        if (_legacy || ForceLegacy) { DeformIntoCoreLegacy(currentWorldMatrices, positions, normals); return; }
        DeformIntoFast(currentWorldMatrices, positions, normals);
    }

    private void PrepareBones(ReadOnlySpan<Matrix4x4> currentWorldMatrices)
    {
        if (!Matrix4x4.Invert(currentWorldMatrices[_meshNodeIndex], out var inverseCurrentMesh))
            throw new InvalidOperationException("The current mesh transform is singular.");
        for (var boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
        {
            var binding = _bones[boneIndex];
            var skin = binding.InverseBind * currentWorldMatrices[binding.NodeIndex] * inverseCurrentMesh;
            _skinMatrices[boneIndex] = skin;
            _normalMatrices[boneIndex] = skin;
            // Same operations as TransformNormal, hoisted out of the per-vertex loop.
            var x = Vector3.TransformNormal(Vector3.UnitX, skin);
            var y = Vector3.TransformNormal(Vector3.UnitY, skin);
            var z = Vector3.TransformNormal(Vector3.UnitZ, skin);
            _normalBasis[boneIndex] = new NormalBasis(Vector3.Cross(y, z), Vector3.Cross(z, x), Vector3.Cross(x, y));
        }
        _skinMatricesPrepared = true;
    }

    private void DeformIntoFast(ReadOnlySpan<Matrix4x4> currentWorldMatrices,
        Span<B3dVector3> positions, Span<B3dVector3> normals)
    {
        PrepareBones(currentWorldMatrices);
        var wantNormals = !normals.IsEmpty;
        var skinMatrices = _skinMatrices;
        var basis = _normalBasis;
        var start = _influenceStart;
        var bones = _influenceBone;
        var weights = _influenceWeight;
        for (var v = 0; v < positions.Length; v++)
        {
            var first = start[v];
            var end = start[v + 1];
            var source = _srcPosition[v];
            if (first == end)
            {
                var p0 = Vector3.Transform(source, skinMatrices[0]);
                positions[v] = new B3dVector3(p0.X, p0.Y, p0.Z);
                if (wantNormals)
                    normals[v] = _hasNormal[v] ? ToB3d(ApplyBasis(basis[0], _srcNormal[v])) : default;
                continue;
            }
            var position = Vector3.Zero;
            var normalSum = Vector3.Zero;
            var hasNormal = wantNormals && _hasNormal[v];
            var sourceNormal = _srcNormal[v];
            for (var i = first; i < end; i++)
            {
                var bone = bones[i];
                var weight = weights[i];
                position += Vector3.Transform(source, skinMatrices[bone]) * weight;
                if (hasNormal) normalSum += ApplyBasis(basis[bone], sourceNormal) * weight;
            }
            positions[v] = new B3dVector3(position.X, position.Y, position.Z);
            if (hasNormal)
            {
                if (end - first > 1 && normalSum.LengthSquared() > 0) normalSum = Vector3.Normalize(normalSum);
                normals[v] = new B3dVector3(normalSum.X, normalSum.Y, normalSum.Z);
            }
            else if (wantNormals) normals[v] = default;
        }
    }

    private static Vector3 ApplyBasis(in NormalBasis b, Vector3 n) => b.YZ * n.X + b.ZX * n.Y + b.XY * n.Z;

    private void DeformIntoCoreLegacy(ReadOnlySpan<Matrix4x4> currentWorldMatrices,
        Span<B3dVector3> positions, Span<B3dVector3> normals)
    {
        var vertices = MeshNode.Mesh!.Vertices;
        if (!Matrix4x4.Invert(currentWorldMatrices[_meshNodeIndex], out var inverseCurrentMesh))
            throw new InvalidOperationException("The current mesh transform is singular.");
        for (var boneIndex = 0; boneIndex < _bones.Length; boneIndex++)
        {
            var binding = _bones[boneIndex];
            var currentBone = currentWorldMatrices[binding.NodeIndex];
            var skin = binding.InverseBind * currentBone * inverseCurrentMesh;
            _skinMatrices[boneIndex] = skin;
            _normalMatrices[boneIndex] = skin;
        }
        _skinMatricesPrepared = true;

        for (var vertexIndex = 0; vertexIndex < vertices.Count; vertexIndex++)
        {
            var vertex = vertices[vertexIndex];
            var influences = _influences[vertexIndex];
            if (influences.Length == 0)
            {
                // Unweighted vertices follow the mesh transform, matching source bone index zero.
                positions[vertexIndex] = TransformPosition(vertex.Position, _skinMatrices[0]);
                if (!normals.IsEmpty && vertex.Normal is { } normal)
                    normals[vertexIndex] = TransformNormal(normal, _normalMatrices[0]);
                else if (!normals.IsEmpty)
                    normals[vertexIndex] = default;
                continue;
            }

            var position = Vector3.Zero;
            var normalSum = Vector3.Zero;
            foreach (var influence in influences)
            {
                var matrix = _skinMatrices[influence.BoneIndex];
                position += ToVector3(TransformPosition(vertex.Position, matrix)) * influence.Weight;
                if (!normals.IsEmpty && vertex.Normal is { } sourceNormal)
                    normalSum += ToVector3(TransformNormal(sourceNormal, _normalMatrices[influence.BoneIndex])) * influence.Weight;
            }
            positions[vertexIndex] = ToB3d(position);
            if (!normals.IsEmpty && vertex.Normal is not null)
            {
                // Upstream leaves the one-bone normal unnormalized; blended normals are normalized.
                if (influences.Length > 1 && normalSum.LengthSquared() > 0)
                    normalSum = Vector3.Normalize(normalSum);
                normals[vertexIndex] = ToB3d(normalSum);
            }
            else if (!normals.IsEmpty)
            {
                normals[vertexIndex] = default;
            }
        }
    }

    /// <summary>Deforms caller-generated source normals with the most recently prepared frame matrices.</summary>
    public void DeformNormalsInto(ReadOnlySpan<B3dVector3> sourceNormals, Span<B3dVector3> normals)
    {
        if (!_skinMatricesPrepared)
            throw new InvalidOperationException("Deform mesh positions before deforming caller-generated normals.");
        var vertices = MeshNode.Mesh!.Vertices;
        if (sourceNormals.Length != vertices.Count)
            throw new ArgumentException("Source normal length must match the mesh vertex count.", nameof(sourceNormals));
        if (normals.Length != vertices.Count)
            throw new ArgumentException("Normal output length must match the mesh vertex count.", nameof(normals));

        if (!(_legacy || ForceLegacy))
        {
            for (var v = 0; v < normals.Length; v++)
            {
                var first = _influenceStart[v];
                var end = _influenceStart[v + 1];
                var n = new Vector3(sourceNormals[v].X, sourceNormals[v].Y, sourceNormals[v].Z);
                if (first == end) { normals[v] = ToB3d(ApplyBasis(_normalBasis[0], n)); continue; }
                var sum = Vector3.Zero;
                for (var i = first; i < end; i++)
                    sum += ApplyBasis(_normalBasis[_influenceBone[i]], n) * _influenceWeight[i];
                if (end - first > 1 && sum.LengthSquared() > 0) sum = Vector3.Normalize(sum);
                normals[v] = ToB3d(sum);
            }
            return;
        }
        for (var vertexIndex = 0; vertexIndex < vertices.Count; vertexIndex++)
        {
            var influences = _influences[vertexIndex];
            if (influences.Length == 0)
            {
                normals[vertexIndex] = TransformNormal(sourceNormals[vertexIndex], _normalMatrices[0]);
                continue;
            }

            var normalSum = Vector3.Zero;
            foreach (var influence in influences)
                normalSum += ToVector3(TransformNormal(sourceNormals[vertexIndex], _normalMatrices[influence.BoneIndex])) * influence.Weight;
            if (influences.Length > 1 && normalSum.LengthSquared() > 0)
                normalSum = Vector3.Normalize(normalSum);
            normals[vertexIndex] = ToB3d(normalSum);
        }
    }

    private static VertexInfluence[][] BuildInfluences(B3dMesh mesh, List<int> boneNodes, B3dPose pose)
    {
        var perVertex = new List<VertexInfluence>[mesh.Vertices.Count];
        var sourceNodes = pose.SourceNodes;
        for (var vertex = 0; vertex < perVertex.Length; vertex++) perVertex[vertex] = [];
        for (var boneIndex = 1; boneIndex < boneNodes.Count; boneIndex++)
        {
            var bone = sourceNodes[boneNodes[boneIndex]];
            foreach (var weight in bone.BoneWeights)
            {
                if ((uint)weight.VertexIndex >= (uint)mesh.Vertices.Count)
                    throw new ArgumentException($"Bone '{bone.Name}' references vertex {weight.VertexIndex}, outside the mesh.");
                if (!float.IsFinite(weight.Weight) || weight.Weight < 0)
                    throw new ArgumentException($"Bone '{bone.Name}' has an invalid weight.");

                var influences = perVertex[weight.VertexIndex];
                var insertAt = 0;
                while (insertAt < influences.Count && weight.Weight <= influences[insertAt].Weight) insertAt++;
                if (insertAt >= 4) continue;
                influences.Insert(insertAt, new VertexInfluence(boneIndex, weight.Weight));
                if (influences.Count > 4) influences.RemoveAt(4);
            }
        }

        var result = new VertexInfluence[perVertex.Length][];
        for (var vertex = 0; vertex < result.Length; vertex++)
        {
            var influences = perVertex[vertex];
            var sum = 0f;
            for (var i = 0; i < influences.Count; i++) sum += influences[i].Weight;
            if (sum > 0)
            {
                result[vertex] = new VertexInfluence[influences.Count];
                for (var i = 0; i < influences.Count; i++)
                    result[vertex][i] = influences[i] with { Weight = influences[i].Weight / sum };
            }
            else
            {
                if (influences.Count > 0)
                    throw new ArgumentException($"Vertex {vertex} has bone weights with a zero total.");
                result[vertex] = [];
            }
        }
        return result;
    }

    private static Matrix4x4[] BuildBindMatrices(B3dPose pose)
    {
        var matrices = new Matrix4x4[pose.Count];
        var sourceNodes = pose.SourceNodes;
        var parents = pose.ParentIndices;
        for (var index = 0; index < pose.Count; index++)
        {
            var node = sourceNodes[index];
            var local = B3dAnimationEvaluator.ComposeLocalMatrix(node.Position, node.Scale, node.Rotation);
            var parent = parents[index];
            matrices[index] = parent < 0 ? local : local * matrices[parent];
        }
        return matrices;
    }

    private static int FindNodeIndex(B3dPose pose, B3dNode node)
    {
        for (var index = 0; index < pose.Count; index++)
            if (ReferenceEquals(pose.SourceNodes[index], node)) return index;
        throw new ArgumentException("The mesh node does not belong to the pose model.", nameof(node));
    }

    private static bool IsDescendantInMeshScope(B3dPose pose, int index, int ancestor)
    {
        var parents = pose.ParentIndices;
        var sourceNodes = pose.SourceNodes;
        for (var parent = parents[index]; parent >= 0; parent = parents[parent])
        {
            if (parent == ancestor) return true;
            if (sourceNodes[parent].Mesh is not null) return false;
        }
        return false;
    }

    private static B3dVector3 TransformPosition(B3dVector3 value, Matrix4x4 matrix) =>
        ToB3d(Vector3.Transform(new Vector3(value.X, value.Y, value.Z), matrix));

    private static Vector3 ToVector3(B3dVector3 value) => new(value.X, value.Y, value.Z);

    private static B3dVector3 TransformNormal(B3dVector3 value, Matrix4x4 matrix)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, matrix);
        var y = Vector3.TransformNormal(Vector3.UnitY, matrix);
        var z = Vector3.TransformNormal(Vector3.UnitZ, matrix);
        var normal = new Vector3(value.X, value.Y, value.Z);
        return ToB3d(Vector3.Cross(y, z) * normal.X + Vector3.Cross(z, x) * normal.Y + Vector3.Cross(x, y) * normal.Z);
    }

    private static B3dVector3 ToB3d(Vector3 value) => new(value.X, value.Y, value.Z);
    private readonly record struct NormalBasis(Vector3 YZ, Vector3 ZX, Vector3 XY);
    private readonly record struct BoneBinding(int NodeIndex, Matrix4x4 InverseBind);
    private readonly record struct VertexInfluence(int BoneIndex, float Weight);
}
