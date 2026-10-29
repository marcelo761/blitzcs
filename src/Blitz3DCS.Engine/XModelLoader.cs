using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Blitz3DCS.Engine;

/// <summary>Loads the static geometry subset of DirectX X files used by Blitz3D LoadMesh.</summary>
/// <remarks>When requested, <paramref name="preserveAnimation"/> rejects keyed animation or skin weights that this loader cannot retain.</remarks>
public static class XModelLoader
{
    private const int MaximumExpandedBytes = 256 * 1024 * 1024;

    public static B3dModel Load(Stream source, bool preserveAnimation = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];
        int read;
        while ((read = source.Read(readBuffer, 0, readBuffer.Length)) != 0)
        {
            if (buffer.Length > MaximumExpandedBytes - read)
                throw new InvalidDataException("The X file exceeds the maximum supported size.");
            buffer.Write(readBuffer, 0, read);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("xof "u8))
            throw new InvalidDataException("The file is not a DirectX X file.");
        var floatBits = Encoding.ASCII.GetString(bytes, 12, 4);
        if (floatBits is not ("0032" or "0064")) throw new InvalidDataException("Unsupported X floating-point size.");
        var format = Encoding.ASCII.GetString(bytes, 8, 4);
        var data = bytes.AsSpan(16).ToArray();
        var floatsAreDouble = floatBits == "0064";
        IReadOnlyList<XObject> objects = format switch
        {
            "txt " => new XTextReader(Encoding.ASCII.GetString(data)).Read(),
            "bin " => new XBinaryReader(data, floatsAreDouble).Read(),
            "bzip" => new XBinaryReader(InflateMsZip(bytes), floatsAreDouble).Read(),
            "tzip" => throw new InvalidDataException("Compressed text X files are not supported."),
            _ => throw new InvalidDataException($"Unsupported X encoding '{format}'.")
        };
        if (preserveAnimation && HasUnsupportedAnimationOrSkinning(objects))
            throw new NotSupportedException("X animation keys and skin weights cannot be preserved by this loader.");
        return BuildModel(objects);
    }

    private static byte[] InflateMsZip(byte[] file)
    {
        if (file.Length < 22) throw new InvalidDataException("The compressed X header is truncated.");
        var totalWithHeader = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16, 4));
        if (totalWithHeader < 16 || totalWithHeader > MaximumExpandedBytes)
            throw new InvalidDataException("The compressed X expanded size is invalid.");
        using var output = new MemoryStream();
        var offset = 20;
        while (offset < file.Length)
        {
            if (file.Length - offset < 4) throw new InvalidDataException("The compressed X chunk header is truncated.");
            var expanded = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset, 2));
            var compressed = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset + 2, 2));
            offset += 4;
            if (expanded == 0 || expanded > 32768 || compressed < 3 || compressed > file.Length - offset)
                throw new InvalidDataException("The compressed X chunk sizes are invalid.");
            if (file[offset] != (byte)'C' || file[offset + 1] != (byte)'K')
                throw new InvalidDataException("The compressed X chunk has no MSZIP signature.");

            // Each X MSZIP chunk is a raw DEFLATE stream prefixed by CK. The shipped SCP X asset
            // uses a single chunk; DeflateStream handles that native MSZIP stream directly.
            using var compressedStream = new MemoryStream(file, offset + 2, compressed - 2, writable: false);
            using var inflater = new DeflateStream(compressedStream, CompressionMode.Decompress);
            var expandedLimit = checked((int)totalWithHeader - 16);
            if (expanded > expandedLimit - output.Length)
                throw new InvalidDataException("The compressed X chunk exceeds the declared expanded size.");
            InflateChunk(inflater, output, expanded);
            offset += compressed;
        }
        if (output.Length + 16 != totalWithHeader)
            throw new InvalidDataException("The compressed X file expanded to an unexpected total size.");
        return output.ToArray();
    }

    private static void InflateChunk(DeflateStream inflater, MemoryStream output, int expectedBytes)
    {
        var buffer = new byte[Math.Min(8192, expectedBytes)];
        var remaining = expectedBytes;
        while (remaining > 0)
        {
            var read = inflater.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) throw new InvalidDataException("The compressed X chunk expanded to an unexpected size.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (inflater.Read(buffer, 0, 1) != 0)
            throw new InvalidDataException("The compressed X chunk expanded beyond its declared size.");
    }

    private static bool HasUnsupportedAnimationOrSkinning(IReadOnlyList<XObject> objects)
    {
        var pending = new Stack<XObject>(objects);
        while (pending.TryPop(out var item))
        {
            if (item.Identifier.Equals("AnimationKey", StringComparison.OrdinalIgnoreCase) &&
                item.Numbers.Count >= 2 && item.Numbers[1] > 0)
                return true;
            if (item.Identifier.Equals("SkinWeights", StringComparison.OrdinalIgnoreCase) &&
                item.Numbers.Count > 0 && item.Numbers[0] > 0)
                return true;
            foreach (var child in item.Children) pending.Push(child);
        }
        return false;
    }

    private static B3dModel BuildModel(IReadOnlyList<XObject> objects)
    {
        var textures = new List<B3dTexture>();
        var brushes = new List<B3dBrush>();
        var materialDefinitions = new Dictionary<string, XObject>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<XObject>(objects);
        while (pending.TryPop(out var item))
        {
            if (item.Identifier.Equals("Material", StringComparison.OrdinalIgnoreCase) && item.Name.Length > 0)
                materialDefinitions.TryAdd(item.Name, item);
            foreach (var child in item.Children) pending.Push(child);
        }
        var root = IdentityNode("");
        foreach (var item in objects)
        {
            if (item.Identifier.Equals("Frame", StringComparison.OrdinalIgnoreCase))
                root.Children.Add(BuildFrame(item, textures, brushes, materialDefinitions));
            else if (item.Identifier.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
                root.Mesh = BuildMesh(item, textures, brushes, materialDefinitions);
        }
        return new B3dModel(0, root, textures, brushes, null, null);
    }

    private static B3dNode IdentityNode(string name) => new()
    {
        Name = name,
        Scale = new B3dVector3(1, 1, 1),
        Rotation = new B3dQuaternion(1, 0, 0, 0)
    };

    private static B3dNode BuildFrame(XObject source, List<B3dTexture> textures, List<B3dBrush> brushes,
        IReadOnlyDictionary<string, XObject> materialDefinitions)
    {
        var node = IdentityNode(source.Name);
        foreach (var child in source.Children)
        {
            if (child.Identifier.Equals("FrameTransformMatrix", StringComparison.OrdinalIgnoreCase))
            {
                var m = child.Numbers.Select(value => (float)value).ToArray();
                if (m.Length < 16) throw new InvalidDataException("FrameTransformMatrix requires 16 values.");
                var matrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position))
                    throw new InvalidDataException("A frame transform cannot be decomposed.");
                node.Position = V(position);
                node.Scale = V(scale);
                node.Rotation = new B3dQuaternion(rotation.W, rotation.X, rotation.Y, rotation.Z);
            }
            else if (child.Identifier.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
                node.Mesh = BuildMesh(child, textures, brushes, materialDefinitions);
            else if (child.Identifier.Equals("Frame", StringComparison.OrdinalIgnoreCase))
                node.Children.Add(BuildFrame(child, textures, brushes, materialDefinitions));
        }
        return node;
    }

    private static B3dMesh BuildMesh(XObject source, List<B3dTexture> textures, List<B3dBrush> brushes,
        IReadOnlyDictionary<string, XObject> materialDefinitions)
    {
        var values = source.Numbers;
        var cursor = 0;
        var vertexCount = ReadCount(values, ref cursor, "vertex");
        var positions = new B3dVector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            positions[i] = new B3dVector3(ReadFloat(values, ref cursor), ReadFloat(values, ref cursor), ReadFloat(values, ref cursor));
        var faceCount = ReadCount(values, ref cursor, "face");
        var faces = new List<int[]>(faceCount);
        for (var i = 0; i < faceCount; i++)
        {
            var count = ReadCount(values, ref cursor, "face vertex");
            if (count < 3) throw new InvalidDataException("An X mesh face must contain at least three vertices.");
            var face = new int[count];
            for (var j = 0; j < count; j++)
            {
                face[j] = ReadCount(values, ref cursor, "vertex index");
                if ((uint)face[j] >= (uint)vertexCount) throw new InvalidDataException("An X mesh index is outside its vertex array.");
            }
            faces.Add(face);
        }

        B3dVector3[]? normals = null;
        int[][]? normalFaces = null;
        var uv = new B3dVector2[vertexCount];
        var materialIndices = new int[faceCount];
        var materialObjects = new List<XObject>();
        foreach (var child in source.Children)
        {
            switch (child.Identifier.ToLowerInvariant())
            {
                case "meshnormals":
                    (normals, normalFaces) = ReadNormals(child, faces);
                    break;
                case "meshtexturecoords":
                    ReadUvs(child, uv);
                    break;
                case "meshmateriallist":
                    ReadMaterials(child, materialIndices, materialObjects, materialDefinitions);
                    break;
            }
        }

        var localMaterials = new List<int>();
        foreach (var material in materialObjects)
            localMaterials.Add(AddMaterial(material, textures, brushes));
        if (localMaterials.Count == 0)
            localMaterials.Add(AddMaterial(null, textures, brushes));

        var result = new B3dMesh { VertexFlags = normals is null ? 0 : 1, TextureCoordinateSize = 2 };
        for (var i = 0; i < vertexCount; i++)
            result.Vertices.Add(new B3dVertex(positions[i], null, null, [[uv[i].X, uv[i].Y]]));

        var mappedFaces = faces;
        if (normals is not null && normalFaces is not null)
            mappedFaces = ApplyCornerNormals(result.Vertices, positions, uv, normals, normalFaces, faces);

        var groups = new Dictionary<int, B3dTriangleGroup>();
        for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            var materialIndex = materialIndices[faceIndex];
            if ((uint)materialIndex >= (uint)localMaterials.Count)
                throw new InvalidDataException("An X face references a missing material.");
            var brushId = localMaterials[materialIndex];
            if (!groups.TryGetValue(brushId, out var group))
            {
                group = new B3dTriangleGroup(brushId);
                groups.Add(brushId, group);
                result.TriangleGroups.Add(group);
            }
            var face = mappedFaces[faceIndex];
            for (var i = 2; i < face.Length; i++) group.Triangles.Add(new B3dTriangle(face[0], face[i - 1], face[i]));
        }
        return result;
    }

    private static (B3dVector3[] Normals, int[][] Faces) ReadNormals(XObject source, IReadOnlyList<int[]> faces)
    {
        var values = source.Numbers;
        var cursor = 0;
        var count = ReadCount(values, ref cursor, "normal");
        var parsed = new B3dVector3[count];
        for (var i = 0; i < count; i++)
            parsed[i] = Normalize(new B3dVector3(ReadFloat(values, ref cursor), ReadFloat(values, ref cursor), ReadFloat(values, ref cursor)));
        var faceCount = ReadCount(values, ref cursor, "normal face");
        if (faceCount != faces.Count) throw new InvalidDataException("An X normal face count must match its mesh.");
        var normalFaces = new int[faceCount][];
        for (var i = 0; i < faceCount; i++)
        {
            var cornerCount = ReadCount(values, ref cursor, "normal face corner");
            if (cornerCount != faces[i].Length) throw new InvalidDataException("An X normal face corner count must match its mesh face.");
            normalFaces[i] = new int[cornerCount];
            for (var j = 0; j < cornerCount; j++)
            {
                var normalIndex = ReadCount(values, ref cursor, "normal index");
                if ((uint)normalIndex >= (uint)parsed.Length) throw new InvalidDataException("An X normal index is invalid.");
                normalFaces[i][j] = normalIndex;
            }
        }
        return (parsed, normalFaces);
    }

    private static List<int[]> ApplyCornerNormals(List<B3dVertex> vertices, B3dVector3[] positions, B3dVector2[] uv,
        B3dVector3[] normals, int[][] normalFaces, IReadOnlyList<int[]> faces)
    {
        var primaryNormalByVertex = new int[positions.Length];
        Array.Fill(primaryNormalByVertex, -1);
        var mappedIndices = new Dictionary<(int Vertex, int Normal), int>();
        var remappedFaces = new List<int[]>(normalFaces.Length);
        for (var faceIndex = 0; faceIndex < normalFaces.Length; faceIndex++)
        {
            var remapped = new int[normalFaces[faceIndex].Length];
            for (var corner = 0; corner < remapped.Length; corner++)
            {
                var vertexIndex = faces[faceIndex][corner];
                var normalIndex = normalFaces[faceIndex][corner];
                var key = (vertexIndex, normalIndex);
                if (!mappedIndices.TryGetValue(key, out var outputIndex))
                {
                    if (primaryNormalByVertex[vertexIndex] < 0)
                    {
                        primaryNormalByVertex[vertexIndex] = normalIndex;
                        outputIndex = vertexIndex;
                        vertices[outputIndex] = vertices[outputIndex] with { Normal = normals[normalIndex] };
                    }
                    else if (primaryNormalByVertex[vertexIndex] == normalIndex)
                    {
                        outputIndex = vertexIndex;
                    }
                    else
                    {
                        outputIndex = vertices.Count;
                        vertices.Add(new B3dVertex(positions[vertexIndex], normals[normalIndex], null,
                            [[uv[vertexIndex].X, uv[vertexIndex].Y]]));
                    }
                    mappedIndices.Add(key, outputIndex);
                }
                remapped[corner] = outputIndex;
            }
            remappedFaces.Add(remapped);
        }
        return remappedFaces;
    }

    private static void ReadUvs(XObject source, B3dVector2[] uv)
    {
        var values = source.Numbers;
        var cursor = 0;
        var count = ReadCount(values, ref cursor, "texture coordinate");
        if (count != uv.Length) throw new InvalidDataException("An X mesh UV count must match its vertex count.");
        for (var i = 0; i < count; i++) uv[i] = new(ReadFloat(values, ref cursor), ReadFloat(values, ref cursor));
    }

    private static void ReadMaterials(XObject source, int[] faceMaterials, List<XObject> materials,
        IReadOnlyDictionary<string, XObject> materialDefinitions)
    {
        var values = source.Numbers;
        var cursor = 0;
        var materialCount = ReadCount(values, ref cursor, "material");
        var faceCount = ReadCount(values, ref cursor, "material face");
        if (faceCount != faceMaterials.Length) throw new InvalidDataException("An X material face count must match its mesh.");
        for (var i = 0; i < faceCount; i++) faceMaterials[i] = ReadCount(values, ref cursor, "face material index");
        foreach (var child in source.Children)
        {
            if (child.Identifier.Equals("Material", StringComparison.OrdinalIgnoreCase))
                materials.Add(child);
            else if (child.Identifier.Length == 0 && child.Strings.FirstOrDefault() is { } name)
                materials.Add(materialDefinitions.GetValueOrDefault(name, XObject.EmptyMaterial));
        }
        if (materials.Count > materialCount) throw new InvalidDataException("An X material list has more materials than declared.");
        while (materials.Count < materialCount) materials.Add(XObject.EmptyMaterial);
    }

    private static int AddMaterial(XObject? source, List<B3dTexture> textures, List<B3dBrush> brushes)
    {
        var numbers = source?.Numbers ?? [];
        var alpha = numbers.Count >= 4 && numbers[3] != 0 ? (float)numbers[3] : 1;
        var color = numbers.Count >= 4 ? new B3dColor((float)numbers[0], (float)numbers[1], (float)numbers[2], alpha) : new B3dColor(1, 1, 1, 1);
        var textureFile = source?.Children.FirstOrDefault(child => child.Identifier.Equals("TextureFilename", StringComparison.OrdinalIgnoreCase))?.Strings.FirstOrDefault();
        var textureIds = Array.Empty<int>();
        if (!string.IsNullOrEmpty(textureFile))
        {
            textureIds = [textures.Count];
            textures.Add(new B3dTexture(textureFile, 0, 2, new(0, 0), new(1, 1), 0));
            color = new B3dColor(1, 1, 1, color.A);
        }
        // The Blitz3D X loader reads face color and alpha only; X power/specular/emissive are ignored.
        brushes.Add(new B3dBrush(source?.Name ?? "", color, 0, 0, 0, textureIds));
        return brushes.Count - 1;
    }

    private static int ReadCount(IReadOnlyList<double> values, ref int cursor, string label)
    {
        var value = ReadFloat(values, ref cursor);
        if (value < 0 || value > 10_000_000 || value != Math.Truncate(value)) throw new InvalidDataException($"Invalid X {label} count or index.");
        return (int)value;
    }

    private static float ReadFloat(IReadOnlyList<double> values, ref int cursor)
    {
        if ((uint)cursor >= (uint)values.Count) throw new InvalidDataException("X numeric data ended unexpectedly.");
        var value = values[cursor++];
        if (!double.IsFinite(value) || value < -float.MaxValue || value > float.MaxValue) throw new InvalidDataException("X contains a non-finite or unrepresentable number.");
        return (float)value;
    }

    private static B3dVector3 V(Vector3 value) => new(value.X, value.Y, value.Z);
    private static B3dVector3 Normalize(B3dVector3 value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        return length > 0 ? new(value.X / length, value.Y / length, value.Z / length) : value;
    }

    private sealed class XObject(string identifier, string name)
    {
        public static readonly XObject EmptyMaterial = new("Material", "");
        public string Identifier { get; } = identifier;
        public string Name { get; } = name;
        public List<double> Numbers { get; } = [];
        public List<string> Strings { get; } = [];
        public List<XObject> Children { get; } = [];
    }

    private sealed class XTextReader(string text)
    {
        private readonly string[] tokens = Tokenize(text);
        private int position;

        public IReadOnlyList<XObject> Read()
        {
            var result = new List<XObject>();
            var ignoredTopLevelClosures = 0;
            while (position < tokens.Length)
            {
                if (tokens[position] == "template") { SkipTemplate(); continue; }
                if (tokens[position] == "}")
                {
                    // Some exported static meshes close the geometry block before their trailing animation set.
                    if (++ignoredTopLevelClosures > 1) throw new InvalidDataException("X data contains too many unmatched top-level braces.");
                    position++;
                    continue;
                }
                result.Add(ReadObject());
            }
            return result;
        }

        private XObject ReadObject()
        {
            var identifier = Take();
            var name = "";
            if (position < tokens.Length && tokens[position] != "{") name = Take();
            Expect("{");
            var result = new XObject(identifier, name);
            while (position < tokens.Length && tokens[position] != "}")
            {
                if (tokens[position].StartsWith('\0')) result.Strings.Add(Take()[1..]);
                else if (tokens[position] == "{") result.Children.Add(ReadReference());
                else if (IsObjectStart()) result.Children.Add(ReadObject());
                else
                {
                    var token = Take();
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) result.Numbers.Add(number);
                }
            }
            Expect("}");
            return result;
        }

        private XObject ReadReference()
        {
            Expect("{");
            var reference = new XObject("", "");
            while (position < tokens.Length && tokens[position] != "}")
            {
                var value = Take();
                if (!string.IsNullOrWhiteSpace(value)) reference.Strings.Add(value);
            }
            Expect("}");
            return reference;
        }

        private bool IsObjectStart()
        {
            var token = tokens[position];
            if (token is "{" or "}" or "<" or ">" or "," or ";" || token.StartsWith('\0') ||
                double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
            var next = position + 1;
            if (next < tokens.Length && tokens[next] != "{") next++;
            return next < tokens.Length && tokens[next] == "{";
        }

        private void SkipTemplate()
        {
            position++;
            var start = Array.IndexOf(tokens, "{", position);
            if (start < 0) throw new InvalidDataException("An X template has no body.");
            position = start;
            SkipBalanced();
        }

        private void SkipBalanced()
        {
            var depth = 0;
            do
            {
                var token = Take();
                if (token == "{") depth++;
                else if (token == "}") depth--;
            } while (position < tokens.Length && depth > 0);
            if (depth != 0) throw new InvalidDataException("An X template is incomplete.");
        }

        private string Take() => position < tokens.Length ? tokens[position++] : throw new InvalidDataException($"X data ended unexpectedly at token {position}.");
        private void Expect(string token) { var actual = Take(); if (actual != token) throw new InvalidDataException($"Expected '{token}', found '{actual}' in X data at token {position}."); }

        private static string[] Tokenize(string source)
        {
            var result = new List<string>();
            for (var i = 0; i < source.Length;)
            {
                if (char.IsWhiteSpace(source[i])) { i++; continue; }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    i += 2;
                    while (i < source.Length && source[i] is not ('\r' or '\n')) i++;
                    continue;
                }
                if (source[i] == '"')
                {
                    var end = source.IndexOf('"', i + 1);
                    if (end < 0) throw new InvalidDataException("An X string is unterminated.");
                    result.Add("\0" + source[(i + 1)..end]);
                    i = end + 1;
                    continue;
                }
                if (source[i] is '{' or '}' or '<' or '>' or ',' or ';') { result.Add(source[i++].ToString()); continue; }
                var start = i++;
                while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] is not ('{' or '}' or '<' or '>' or ',' or ';')) i++;
                result.Add(source[start..i]);
            }
            return result.ToArray();
        }
    }

    private sealed class XBinaryReader(byte[] data, bool doubles)
    {
        private const ushort Name = 1, String = 2, Integer = 3, Guid = 5, IntegerList = 6, FloatList = 7;
        private const ushort OpenBrace = 10, CloseBrace = 11, OpenAngle = 16, CloseAngle = 17, Comma = 19, Semicolon = 20, Template = 31;
        private int position;

        public IReadOnlyList<XObject> Read()
        {
            var result = new List<XObject>();
            while (position < data.Length)
            {
                var token = PeekWord();
                if (token == Template) { SkipTemplate(); continue; }
                result.Add(ReadObject());
            }
            return result;
        }

        private XObject ReadObject()
        {
            var identifier = ReadName();
            var name = "";
            if (PeekWord() == Name) name = ReadName();
            Expect(OpenBrace);
            var result = new XObject(identifier, name);
            while (position < data.Length && PeekWord() != CloseBrace)
            {
                var token = ReadWord();
                switch (token)
                {
                    case Name:
                        position -= 2;
                        result.Children.Add(ReadObject());
                        break;
                    case String:
                        position -= 2;
                        result.Strings.Add(ReadString());
                        break;
                    case Integer:
                        result.Numbers.Add(ReadInt32());
                        break;
                    case IntegerList:
                        var intCount = ReadCount32();
                        for (var i = 0; i < intCount; i++) result.Numbers.Add(ReadInt32());
                        break;
                    case FloatList:
                        var floatCount = ReadCount32();
                        for (var i = 0; i < floatCount; i++) result.Numbers.Add(ReadNumber());
                        break;
                    case Guid:
                        Skip(16);
                        break;
                    case OpenBrace:
                        var reference = new XObject("", "");
                        while (PeekWord() != CloseBrace) { var refToken = ReadWord(); if (refToken == Name) reference.Strings.Add(ReadName()); else if (refToken == Guid) Skip(16); else throw new InvalidDataException("Unsupported X data reference."); }
                        Expect(CloseBrace);
                        result.Children.Add(reference);
                        break;
                    case Comma:
                    case Semicolon:
                    case OpenAngle:
                    case CloseAngle:
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported X binary token {token}.");
                }
            }
            Expect(CloseBrace);
            return result;
        }

        private void SkipTemplate()
        {
            ReadWord();
            SkipRecord(ReadWord()); // Template identifier.
            Expect(OpenBrace);
            var depth = 1;
            do
            {
                var token = ReadWord();
                if (token == OpenBrace) depth++;
                else if (token == CloseBrace) depth--;
                else SkipRecord(token);
            } while (position < data.Length && depth > 0);
            if (depth != 0) throw new InvalidDataException("An X binary template is incomplete.");
        }

        private void SkipRecord(ushort token)
        {
            switch (token)
            {
                case Name: SkipSizedString(); break;
                case String: SkipSizedString(); Skip(4); break;
                case Integer: Skip(4); break;
                case Guid: Skip(16); break;
                case IntegerList: Skip(checked(ReadCount32() * 4)); break;
                case FloatList: Skip(checked(ReadCount32() * (doubles ? 8 : 4))); break;
                case 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 50 or 51 or 52: break;
                case 12 or 13 or 14 or 15 or OpenBrace or CloseBrace or OpenAngle or CloseAngle or 18 or Comma or Semicolon: break;
                default: throw new InvalidDataException($"Unsupported X template token {token}.");
            }
        }

        private string ReadName() { Expect(Name); return ReadSizedString(); }
        private string ReadString() { Expect(String); var value = ReadSizedString(); var terminator = ReadWord(); if (terminator is not (Comma or Semicolon)) throw new InvalidDataException("An X string has an invalid terminator."); return value; }
        private string ReadSizedString() { var length = ReadCount32(); Ensure(length); var value = Encoding.ASCII.GetString(data, position, length); Skip(length); return value; }
        private void SkipSizedString() { var length = ReadCount32(); Skip(length); }
        private int ReadCount32() { var value = ReadInt32(); if (value < 0 || value > MaximumExpandedBytes) throw new InvalidDataException("An X binary record size is invalid."); return value; }
        private int ReadInt32() { Ensure(4); var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, 4)); position += 4; return value; }
        private double ReadNumber()
        {
            if (doubles) { Ensure(8); var value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position, 8))); position += 8; return value; }
            Ensure(4); var single = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, 4))); position += 4; return single;
        }
        private ushort PeekWord() { Ensure(2); return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2)); }
        private ushort ReadWord() { var value = PeekWord(); position += 2; return value; }
        private void Expect(ushort expected) { var actual = ReadWord(); if (actual != expected) throw new InvalidDataException($"Expected X binary token {expected}, found {actual} at byte {position - 2}."); }
        private void Skip(int count) { Ensure(count); position += count; }
        private void Ensure(int count) { if (count < 0 || count > data.Length - position) throw new InvalidDataException("X binary data ends unexpectedly."); }
    }
}
