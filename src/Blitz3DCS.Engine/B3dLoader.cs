using System.Buffers.Binary;
using System.Text;

namespace Blitz3DCS.Engine;

/// <summary>Reads Blitz3D B3D model files into CPU-only data.</summary>
public static class B3dLoader
{
    private const int MaximumFileSize = 512 * 1024 * 1024;
    private static readonly Encoding Ansi = CreateAnsiEncoding();

    /// <summary>Loads a B3D file. The stream remains open and is read from its current position.</summary>
    public static B3dModel Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = stream.Read(block, 0, block.Length)) != 0)
        {
            if (buffer.Length + count > MaximumFileSize)
                throw new InvalidDataException($"B3D input exceeds the {MaximumFileSize}-byte limit.");
            buffer.Write(block, 0, count);
        }
        return Parse(buffer.ToArray());
    }

    /// <summary>Parses an in-memory B3D file.</summary>
    public static B3dModel Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaximumFileSize)
            throw new InvalidDataException($"B3D input exceeds the {MaximumFileSize}-byte limit.");

        var root = new Reader(data, 0, data.Length);
        var header = root.ReadChunk();
        if (header.Tag != "BB3D") throw new InvalidDataException("The input does not begin with a B3D BB3D chunk.");
        if (root.HasData) throw new InvalidDataException("B3D input contains trailing bytes after the BB3D chunk.");
        var version = header.Body.ReadInt();
        if (version > 1) throw new InvalidDataException($"Unsupported B3D version {version}.");
        var parser = new Parser();
        while (header.Body.HasData)
        {
            var chunk = header.Body.ReadChunk();
            switch (chunk.Tag)
            {
                case "TEXS": parser.ReadTextures(chunk.Body); break;
                case "BRUS": parser.ReadBrushes(chunk.Body); break;
                case "ANIM": parser.FileAnimation = Parser.ReadAnimationHeader(chunk.Body); break;
                case "NODE":
                    if (parser.Root is not null) throw new InvalidDataException("B3D contains more than one root NODE.");
                    parser.Root = parser.ReadNode(chunk.Body);
                    break;
            }
        }
        if (parser.Root is null) throw new InvalidDataException("B3D contains no root NODE.");
        parser.ValidateReferences(parser.Root);
        return new B3dModel(version, parser.Root, parser.Textures, parser.Brushes, parser.Root.Animation, parser.FileAnimation);
    }

    private static Encoding CreateAnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private sealed class Parser
    {
        public B3dNode? Root { get; set; }
        public List<B3dTexture> Textures { get; } = [];
        public List<B3dBrush> Brushes { get; } = [];
        public B3dAnimation? FileAnimation { get; set; }

        public void ReadTextures(Reader reader)
        {
            while (reader.HasData)
            {
                var name = reader.ReadString();
                var flags = reader.ReadInt();
                var blend = reader.ReadInt();
                var position = new B3dVector2(reader.ReadFloat(), reader.ReadFloat());
                var scale = new B3dVector2(reader.ReadFloat(), reader.ReadFloat());
                var rotation = reader.ReadFloat();
                Textures.Add(new B3dTexture(name, flags, blend, position, scale, rotation));
            }
        }

        public void ReadBrushes(Reader reader)
        {
            var textureSlots = reader.ReadInt();
            if (textureSlots is < 0 or > 8) throw new InvalidDataException($"Invalid B3D brush texture slot count {textureSlots}.");
            while (reader.HasData)
            {
                var name = reader.ReadString();
                var color = new B3dColor(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
                var shininess = reader.ReadFloat();
                var blend = reader.ReadInt();
                var effects = reader.ReadInt();
                var ids = Enumerable.Repeat(-1, 8).ToArray();
                for (var i = 0; i < textureSlots; i++) ids[i] = reader.ReadInt();
                Brushes.Add(new B3dBrush(name, color, shininess, blend, effects, ids));
            }
        }

        public static B3dAnimation ReadAnimationHeader(Reader reader)
        {
            var flags = reader.ReadInt();
            var frames = reader.ReadInt();
            var framesPerSecond = reader.ReadFloat();
            return new B3dAnimation(flags, frames, framesPerSecond);
        }

        public B3dNode ReadNode(Reader reader)
        {
            var node = new B3dNode
            {
                Name = reader.ReadString(),
                Position = new B3dVector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
                Scale = new B3dVector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
                Rotation = new B3dQuaternion(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat())
            };
            while (reader.HasData)
            {
                var chunk = reader.ReadChunk();
                switch (chunk.Tag)
                {
                    case "MESH": node.Mesh = ReadMesh(chunk.Body); break;
                    case "BONE": ReadBone(chunk.Body, node); break;
                    case "KEYS": node.Keys.AddRange(ReadKeys(chunk.Body)); break;
                    case "ANIM": node.Animation = ReadAnimationHeader(chunk.Body); break;
                    case "NODE": node.Children.Add(ReadNode(chunk.Body)); break;
                }
            }
            return node;
        }

        private B3dMesh ReadMesh(Reader reader)
        {
            var mesh = new B3dMesh { BrushId = reader.ReadInt() };
            while (reader.HasData)
            {
                var chunk = reader.ReadChunk();
                switch (chunk.Tag)
                {
                    case "VRTS": ReadVertices(chunk.Body, mesh); break;
                    case "TRIS": ReadTriangles(chunk.Body, mesh); break;
                }
            }
            return mesh;
        }

        private static void ReadVertices(Reader reader, B3dMesh mesh)
        {
            var flags = reader.ReadInt();
            var textureSets = reader.ReadInt();
            var textureSize = reader.ReadInt();
            if (textureSets is < 0 or > 64 || textureSize is < 1 or > 4)
                throw new InvalidDataException("Invalid B3D vertex texture coordinate layout.");
            mesh.VertexFlags = flags;
            mesh.TextureCoordinateSize = textureSize;
            while (reader.HasData)
            {
                var position = new B3dVector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
                B3dVector3? normal = (flags & 1) != 0
                    ? new B3dVector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()) : null;
                B3dColor? color = (flags & 2) != 0
                    ? ReadColor(reader) : null;
                var uvSets = new List<float[]>(textureSets);
                for (var i = 0; i < textureSets; i++)
                {
                    var uv = new float[textureSize];
                    for (var axis = 0; axis < textureSize; axis++) uv[axis] = reader.ReadFloat();
                    uvSets.Add(uv);
                }
                mesh.Vertices.Add(new B3dVertex(position, normal, color, uvSets));
            }
        }

        private static B3dColor ReadColor(Reader reader)
        {
            static float Clamp(float value) => Math.Clamp(value, 0, 1);
            return new B3dColor(Clamp(reader.ReadFloat()), Clamp(reader.ReadFloat()), Clamp(reader.ReadFloat()), Clamp(reader.ReadFloat()));
        }

        private static void ReadTriangles(Reader reader, B3dMesh mesh)
        {
            var brushId = reader.ReadInt();
            var group = new B3dTriangleGroup(brushId);
            while (reader.HasData)
                group.Triangles.Add(new B3dTriangle(reader.ReadInt(), reader.ReadInt(), reader.ReadInt()));
            mesh.TriangleGroups.Add(group);
        }

        private static void ReadBone(Reader reader, B3dNode node)
        {
            node.IsBone = true;
            while (reader.HasData)
                node.BoneWeights.Add(new B3dBoneWeight(reader.ReadInt(), reader.ReadFloat()));
        }

        private static List<B3dAnimationKey> ReadKeys(Reader reader)
        {
            var flags = reader.ReadInt();
            var keys = new List<B3dAnimationKey>();
            while (reader.HasData)
            {
                var frame = reader.ReadInt();
                B3dVector3? position = (flags & 1) != 0
                    ? new B3dVector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()) : null;
                B3dVector3? scale = (flags & 2) != 0
                    ? new B3dVector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()) : null;
                B3dQuaternion? rotation = (flags & 4) != 0
                    ? new B3dQuaternion(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()) : null;
                keys.Add(new B3dAnimationKey(flags, frame, position, scale, rotation));
            }
            return keys;
        }

        public void ValidateReferences(B3dNode root)
        {
            var pending = new Stack<B3dNode>();
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                if (node.Mesh is { } mesh)
                {
                    ValidateBrush(mesh.BrushId, "mesh");
                    foreach (var group in mesh.TriangleGroups)
                    {
                        ValidateBrush(group.BrushId, "triangle group");
                        foreach (var triangle in group.Triangles)
                            if ((uint)triangle.A >= mesh.Vertices.Count || (uint)triangle.B >= mesh.Vertices.Count || (uint)triangle.C >= mesh.Vertices.Count)
                                throw new InvalidDataException("B3D triangle references a vertex outside its mesh.");
                    }
                }
                foreach (var child in node.Children) pending.Push(child);
            }
            foreach (var brush in Brushes)
                foreach (var textureId in brush.TextureIds)
                    if (textureId < -1 || textureId >= Textures.Count)
                        throw new InvalidDataException($"B3D brush references missing texture {textureId}.");
        }

        private void ValidateBrush(int id, string context)
        {
            if (id < -1 || id >= Brushes.Count) throw new InvalidDataException($"B3D {context} references missing brush {id}.");
        }
    }

    private sealed class Reader(ReadOnlyMemory<byte> data, int start, int end)
    {
        private int _position = start;
        public bool HasData => _position < end;
        public int ReadInt()
        {
            Ensure(4);
            var result = BinaryPrimitives.ReadInt32LittleEndian(data.Span[_position..]);
            _position += 4;
            return result;
        }
        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());
        public string ReadString()
        {
            var span = data.Span;
            var terminator = _position;
            while (terminator < end && span[terminator] != 0) terminator++;
            if (terminator == end) throw new InvalidDataException("B3D string has no null terminator inside its chunk.");
            var value = Ansi.GetString(span[_position..terminator]);
            _position = terminator + 1;
            return value;
        }
        public Chunk ReadChunk()
        {
            Ensure(8);
            var tag = Encoding.ASCII.GetString(data.Span.Slice(_position, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[(_position + 4)..]);
            _position += 8;
            if (length > end - _position) throw new InvalidDataException($"B3D chunk {tag} extends beyond its parent chunk.");
            var chunkEnd = _position + (int)length;
            var body = new Reader(data, _position, chunkEnd);
            _position = chunkEnd;
            return new Chunk(tag, body);
        }
        private void Ensure(int count)
        {
            if (count < 0 || count > end - _position) throw new InvalidDataException("B3D data ends inside a chunk value.");
        }
    }

    private readonly record struct Chunk(string Tag, Reader Body);
}

public sealed record B3dModel(int Version, B3dNode Root, IReadOnlyList<B3dTexture> Textures, IReadOnlyList<B3dBrush> Brushes,
    B3dAnimation? Animation, B3dAnimation? FileAnimation);
public sealed class B3dNode
{
    public string Name { get; internal set; } = "";
    public B3dVector3 Position { get; internal set; }
    public B3dVector3 Scale { get; internal set; }
    public B3dQuaternion Rotation { get; internal set; }
    /// <summary>Whether this node is represented by a B3D BONE chunk.</summary>
    public bool IsBone { get; internal set; }
    public B3dMesh? Mesh { get; internal set; }
    public List<B3dNode> Children { get; } = [];
    public List<B3dBoneWeight> BoneWeights { get; } = [];
    public B3dAnimation? Animation { get; internal set; }
    public List<B3dAnimationKey> Keys { get; } = [];
}
public sealed class B3dMesh
{
    public int BrushId { get; internal set; }
    public int VertexFlags { get; internal set; }
    public int TextureCoordinateSize { get; internal set; }
    public List<B3dVertex> Vertices { get; } = [];
    public List<B3dTriangleGroup> TriangleGroups { get; } = [];
}
public sealed record B3dTexture(string Name, int Flags, int Blend, B3dVector2 Position, B3dVector2 Scale, float Rotation);
public sealed record B3dBrush(string Name, B3dColor Color, float Shininess, int Blend, int Effects, IReadOnlyList<int> TextureIds);
public sealed record B3dVertex(B3dVector3 Position, B3dVector3? Normal, B3dColor? Color, IReadOnlyList<float[]> TextureCoordinates);
public sealed class B3dTriangleGroup(int brushId)
{
    public int BrushId { get; } = brushId;
    public List<B3dTriangle> Triangles { get; } = [];
}
public sealed record B3dTriangle(int A, int B, int C);
public sealed record B3dBoneWeight(int VertexIndex, float Weight);
public sealed record B3dAnimation(int Flags, int FrameCount, float FramesPerSecond);
public sealed record B3dAnimationKey(int Flags, int Frame, B3dVector3? Position, B3dVector3? Scale, B3dQuaternion? Rotation);
public readonly record struct B3dVector2(float X, float Y);
public readonly record struct B3dVector3(float X, float Y, float Z);
/// <summary>B3D quaternion components in the serialized W, X, Y, Z order.</summary>
public readonly record struct B3dQuaternion(float W, float X, float Y, float Z);
public readonly record struct B3dColor(float R, float G, float B, float A);
