using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    internal static readonly Dictionary<int, AnimatedModel> AnimatedModels = [];

    /// <summary>Loads a B3D model while preserving its node hierarchy and animation data.</summary>
    private static int LoadAnimMeshCore(string file, int parent)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        CheckThreadIfStarted();
        var path = Path.GetFullPath(file.Replace('\\', Path.DirectorySeparatorChar));
        ValidateModelFormat(path);

        B3dModel model;
        try
        {
            using var stream = File.OpenRead(path);
            model = ReadModelData(stream, path, preserveAnimation: true);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        var pose = new B3dPose(model);
        var playback = new B3dAnimationPlayback(model);
        if (playback.SequenceCount > 0) playback.Animate(B3dAnimationMode.Loop, 0);
        var textures = new int[model.Textures.Count];
        var entities = new List<int>();
        var animatedMeshes = new List<AnimatedMesh>();
        var scene = RequireScene();
        var root = 0;
        try
        {
            LoadModelTextures(model, path, textures);
            AddNode(model.Root, -1);

            B3dAnimationEvaluator.EvaluateInto(model, 0, pose);
            var poseNodes = pose.Nodes;
            for (var index = 0; index < poseNodes.Length; index++)
                scene.ApplyAnimationPose(entities[index], poseNodes[index]);

            // Capture bind data above against the source hierarchy, then attach the evaluated
            // root without changing its local pose, matching LoadAnimMesh's parent semantics.
            if (parent != 0) scene.EntityParent(root, parent, global: false);

            var localPoses = new SceneLocalPose[entities.Count];
            var currentWorld = new Matrix4x4[entities.Count];
            var previousWorld = new Matrix4x4[entities.Count];
            for (var index = 0; index < entities.Count; index++)
            {
                localPoses[index] = scene.GetLocalPose(entities[index]);
                var world = ToWorldMatrix(scene.GetWorldTransform(entities[index]));
                currentWorld[index] = world;
                previousWorld[index] = world;
            }

            var animatedModel = new AnimatedModel(model, entities.ToArray(), pose, playback,
                localPoses, currentWorld, previousWorld, animatedMeshes);
            animatedModel.TransitionDest = poseNodes.ToArray();
            AnimatedModels.Add(root, animatedModel);
            return root;
        }
        catch
        {
            AnimatedModels.Remove(root);
            if (root != 0)
                FreeEntity(root);
            else
                foreach (var entity in entities.AsEnumerable().Reverse())
                    FreeEntity(entity);
            throw;
        }
        finally
        {
            foreach (var texture in textures)
                if (texture != 0) FreeTexture(texture);
        }

        int AddNode(B3dNode node, int parentIndex)
        {
            var entity = node.Mesh is null ? scene.CreatePivot() : CreateMesh();
            if (entities.Count == 0) root = entity;
            entities.Add(entity);
            if (parentIndex >= 0) scene.EntityParent(entity, entities[parentIndex], global: false);
            scene.NameEntity(entity, node.Name);
            scene.SetLocalPose(entity,
                new SceneVector3(node.Position.X, node.Position.Y, node.Position.Z),
                new SceneVector3(node.Scale.X, node.Scale.Y, node.Scale.Z),
                node.Rotation.W, node.Rotation.X, node.Rotation.Y, node.Rotation.Z);

            if (node.Mesh is { } mesh)
            {
                var meshState = GetMesh(entity);
                if (mesh.BrushId >= 0)
                {
                    var replacement = BuildMaterial(model.Brushes[mesh.BrushId], textures);
                    var previous = meshState.Material;
                    meshState.Material = replacement;
                    previous.Dispose();
                }

                var hasBones = HasScopedBone(node);
                var skin = hasBones ? new B3dSkinningBuffer(pose, node) : null;
                var positions = new B3dVector3[mesh.Vertices.Count];
                var normals = new B3dVector3[mesh.Vertices.Count];
                var animatedMesh = new AnimatedMesh(entity, entities.Count - 1, skin, positions, normals);
                foreach (var group in mesh.TriangleGroups)
                {
                    var baseVertices = BuildAnimatedSurfaceVertices(mesh, group);
                    var gpuVertices = baseVertices;
                    int[]? vertexMap = null;
                    uint[]? gpuIndices = null;
                    if (EngineOptimizations.SkinFuse && TryCompactSurface(group, baseVertices, out var compact, out var map, out var remapped))
                    {
                        gpuVertices = compact;
                        vertexMap = map;
                        gpuIndices = remapped;
                    }
                    var surface = CreateSurface(entity);
                    var surfaceState = GetSurface(surface);
                    surfaceState.Vertices.AddRange(baseVertices);
                    foreach (var triangle in group.Triangles)
                    {
                        surfaceState.Indices.Add((uint)triangle.A);
                        surfaceState.Indices.Add((uint)triangle.B);
                        surfaceState.Indices.Add((uint)triangle.C);
                    }
                    if (group.BrushId >= 0)
                        SetSurfaceMaterialForLoading(surface, BuildMaterial(model.Brushes[group.BrushId], textures));
                    animatedMesh.Surfaces.Add(new AnimatedSurface(surface, gpuVertices,
                        (D3D11Vertex[])gpuVertices.Clone(), gpuIndices ?? surfaceState.Indices.ToArray(), vertexMap));
                }
                meshState.Animated = animatedMesh;
                animatedMeshes.Add(animatedMesh);
            }

            var index = entities.Count - 1;
            foreach (var child in node.Children) AddNode(child, index);
            return index;
        }
    }

    /// <summary>Keeps only the vertices a triangle group references (ascending mesh order) and remaps its indices.</summary>
    private static bool TryCompactSurface(B3dTriangleGroup group, D3D11Vertex[] full,
        out D3D11Vertex[] compact, out int[] map, out uint[] indices)
    {
        compact = full; map = []; indices = [];
        if (group.Triangles.Count == 0) return false;
        var remap = new int[full.Length];
        Array.Fill(remap, -1);
        foreach (var t in group.Triangles)
        {
            if ((uint)t.A >= (uint)full.Length || (uint)t.B >= (uint)full.Length || (uint)t.C >= (uint)full.Length) return false;
            remap[t.A] = remap[t.B] = remap[t.C] = 0;
        }
        var used = 0;
        for (var i = 0; i < remap.Length; i++) if (remap[i] == 0) used++;
        map = new int[used];
        compact = new D3D11Vertex[used];
        var slot = 0;
        for (var i = 0; i < remap.Length; i++)
        {
            if (remap[i] != 0) continue;
            map[slot] = i;
            compact[slot] = full[i];
            remap[i] = slot++;
        }
        indices = new uint[group.Triangles.Count * 3];
        var cursor = 0;
        foreach (var t in group.Triangles)
        {
            indices[cursor++] = (uint)remap[t.A];
            indices[cursor++] = (uint)remap[t.B];
            indices[cursor++] = (uint)remap[t.C];
        }
        return true;
    }

    private static D3D11Vertex[] BuildAnimatedSurfaceVertices(B3dMesh mesh, B3dTriangleGroup group)
    {
        var result = new D3D11Vertex[mesh.Vertices.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var source = mesh.Vertices[index];
            var color = source.Color is { } c ? new Vector4(c.R, c.G, c.B, c.A) : Vector4.One;
            var normal = source.Normal is { } n ? new Vector3(n.X, n.Y, n.Z) : Vector3.Zero;
            result[index] = new D3D11Vertex(new Vector3(source.Position.X, source.Position.Y, source.Position.Z),
                color, normal, Uv(source, 0), Uv(source, 1));
        }

        if ((mesh.VertexFlags & 1) == 0)
            GenerateB3dNormals(mesh, group, result, Matrix4x4.Identity);
        return result;
    }

    private static bool HasScopedBone(B3dNode meshNode)
    {
        if (meshNode.IsBone) return true;
        foreach (var child in meshNode.Children)
            if (HasScopedBoneBeforeNestedMesh(child)) return true;
        return false;

        static bool HasScopedBoneBeforeNestedMesh(B3dNode node)
        {
            if (node.IsBone) return true;
            if (node.Mesh is not null) return false;
            foreach (var child in node.Children)
                if (HasScopedBoneBeforeNestedMesh(child)) return true;
            return false;
        }
    }

    internal sealed class AnimatedModel(B3dModel model, int[] entities, B3dPose pose,
        B3dAnimationPlayback playback, SceneLocalPose[] transitionFrom,
        Matrix4x4[] currentWorld, Matrix4x4[] previousWorld, List<AnimatedMesh> meshes)
    {
        public B3dModel Model { get; } = model;
        public int[] Entities { get; } = entities;
        public B3dPose Pose { get; } = pose;
        public B3dAnimationPlayback Playback { get; } = playback;
        public SceneLocalPose[] TransitionFrom { get; } = transitionFrom;
        public Matrix4x4[] CurrentWorld { get; } = currentWorld;
        public Matrix4x4[] PreviousWorld { get; } = previousWorld;
        public List<AnimatedMesh> Meshes { get; } = meshes;
        public bool SkinDirty { get; set; } = true;
        public B3dPoseNode[]? TransitionDest { get; set; }
        public int Root => Entities[0];
    }

    internal sealed class AnimatedMesh(int entity, int nodeIndex, B3dSkinningBuffer? skin,
        B3dVector3[] positions, B3dVector3[] normals)
    {
        public int Entity { get; } = entity;
        public int NodeIndex { get; } = nodeIndex;
        public B3dSkinningBuffer? Skin { get; } = skin;
        public B3dVector3[] Positions { get; } = positions;
        public B3dVector3[] Normals { get; } = normals;
        public List<AnimatedSurface> Surfaces { get; } = [];
        public bool Dirty { get; set; } = true;
        /// <summary>Set by PrepareAnimatedGeometry when this frame's skin was skipped because the mesh is off-screen.</summary>
        public bool SkinCulled { get; set; }
    }

    internal sealed class AnimatedSurface
    {
        /// <param name="vertexMap">Mesh vertex index per GPU vertex slot; null keeps the full mesh copy (identity map).</param>
        public AnimatedSurface(int handle, D3D11Vertex[] baseSurfaceVertices,
            D3D11Vertex[] vertices, uint[] indices, int[]? vertexMap = null)
        {
            Handle = handle;
            BaseSurfaceVertices = baseSurfaceVertices;
            Vertices = vertices;
            Indices = indices;
            SourceNormals = new B3dVector3[baseSurfaceVertices.Length];
            SourceNormalVectors = new Vector3[baseSurfaceVertices.Length];
            for (var index = 0; index < baseSurfaceVertices.Length; index++)
            {
                var normal = baseSurfaceVertices[index].Normal;
                SourceNormals[index] = new B3dVector3(normal.X, normal.Y, normal.Z);
                SourceNormalVectors[index] = normal;
            }
            DeformedNormals = new B3dVector3[baseSurfaceVertices.Length];
            if (vertexMap is null)
            {
                vertexMap = new int[baseSurfaceVertices.Length];
                for (var index = 0; index < vertexMap.Length; index++) vertexMap[index] = index;
                IdentityMap = true;
            }
            VertexMap = vertexMap;
        }

        public int Handle { get; }
        public D3D11Vertex[] BaseSurfaceVertices { get; }
        public D3D11Vertex[] Vertices { get; }
        public uint[] Indices { get; }
        public B3dVector3[] SourceNormals { get; }
        public Vector3[] SourceNormalVectors { get; }
        public B3dVector3[] DeformedNormals { get; }
        /// <summary>Mesh vertex index for each slot of <see cref="Vertices"/>.</summary>
        public int[] VertexMap { get; }
        public bool IdentityMap { get; }
        public D3D11Mesh? GpuMesh { get; set; }
        public bool UploadDirty { get; set; } = true;
    }
}
