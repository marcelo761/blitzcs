using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BlitzCS.BinaryCompiler;
using BlitzCS.Compiler;

namespace BlitzCS.CLI;

internal sealed record Blitz3dRuntimeCommand(
    string BasicName,
    uint Rva,
    ExternalValueType ReturnType,
    IReadOnlyList<ExternalValueType> ParameterTypes,
    IReadOnlyList<object> DefaultValues);

internal static class Blitz3dRuntimeExtractor
{
    internal static IReadOnlyList<Blitz3dRuntimeCommand> ExtractCommands(string runtimeDllPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDllPath);
        if (!File.Exists(runtimeDllPath))
            throw new FileNotFoundException($"Blitz3D runtime.dll not found at '{runtimeDllPath}'.", runtimeDllPath);

        var bytes = File.ReadAllBytes(runtimeDllPath);
        var image = PeReader.Read(bytes);
        var mapped = image.MapImage();

        var export = image.Exports.FirstOrDefault(e => string.Equals(e.Name, "runtimeGetRuntime", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Export 'runtimeGetRuntime' was not found in '{runtimeDllPath}'.");

        var exportRva = (int)export.Rva;
        var movEaxOffset = -1;
        for (var i = 0; i < 64 && exportRva + i + 5 <= mapped.Length; i++)
        {
            if (mapped[exportRva + i] == 0xB8)
            {
                movEaxOffset = i;
                break;
            }
        }
        if (movEaxOffset < 0)
            throw new InvalidOperationException($"Expected 'mov eax, imm32' (0xB8) in export RVA 0x{export.Rva:X}.");

        var runtimeObjVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(exportRva + movEaxOffset + 1, 4));
        var runtimeObjRva = (int)(runtimeObjVa - image.ImageBase);
        if (runtimeObjRva < 0 || runtimeObjRva + 4 > mapped.Length)
            throw new InvalidOperationException($"runtimeObjVa 0x{runtimeObjVa:X} (RVA 0x{runtimeObjRva:X}) is outside mapped image.");

        var vtableVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(runtimeObjRva, 4));
        if (vtableVa == 0)
        {
            for (var i = 0; i < 64 && exportRva + i + 10 <= mapped.Length; i++)
            {
                if (mapped[exportRva + i] == 0xC7 && mapped[exportRva + i + 1] == 0x05)
                {
                    var destVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(exportRva + i + 2, 4));
                    if (destVa == runtimeObjVa)
                    {
                        vtableVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(exportRva + i + 6, 4));
                        break;
                    }
                }
            }
        }
        var vtableRva = (int)(vtableVa - image.ImageBase);
        if (vtableRva < 0 || vtableRva + 8 > mapped.Length)
            throw new InvalidOperationException($"vtableVa 0x{vtableVa:X} (RVA 0x{vtableRva:X}) is outside mapped image.");

        var nextSymVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(vtableRva + 4, 4));
        var nextSymRva = (int)(nextSymVa - image.ImageBase);
        if (nextSymRva < 0 || nextSymRva >= mapped.Length)
            throw new InvalidOperationException($"nextSymVa 0x{nextSymVa:X} (RVA 0x{nextSymRva:X}) is outside mapped image.");

        var callOffset = -1;
        for (var i = 0; i < 64 && nextSymRva + i + 10 <= mapped.Length; i++)
        {
            if (mapped[nextSymRva + i] == 0x68 && mapped[nextSymRva + i + 5] == 0xE8)
            {
                callOffset = i + 5;
                break;
            }
        }
        if (callOffset < 0)
        {
            for (var i = 0; i < 64 && nextSymRva + i + 5 <= mapped.Length; i++)
            {
                if (mapped[nextSymRva + i] == 0xE8)
                {
                    callOffset = i;
                    break;
                }
            }
        }
        if (callOffset < 0)
            throw new InvalidOperationException($"Call instruction (0xE8) not found in nextSym at RVA 0x{nextSymRva:X}.");

        var callSiteRva = nextSymRva + callOffset;
        var rel32 = BinaryPrimitives.ReadInt32LittleEndian(mapped.AsSpan(callSiteRva + 1, 4));
        var bbruntimeLinkRva = callSiteRva + 5 + rel32;
        if (bbruntimeLinkRva < 0 || bbruntimeLinkRva >= mapped.Length)
            throw new InvalidOperationException($"bbruntime_link RVA 0x{bbruntimeLinkRva:X} is outside mapped image.");

        var functionsToScan = new Queue<int>();
        var visitedFunctions = new HashSet<int>();

        functionsToScan.Enqueue(bbruntimeLinkRva);
        visitedFunctions.Add(bbruntimeLinkRva);

        var rawCommands = new List<(string BasicName, uint Rva, ExternalValueType ReturnType, IReadOnlyList<ExternalValueType> ParameterTypes, IReadOnlyList<object> DefaultValues)>();

        while (functionsToScan.Count > 0)
        {
            var funcRva = functionsToScan.Dequeue();
            var pos = funcRva;
            var maxPos = Math.Min(mapped.Length - 12, funcRva + 0x10000);

            while (pos <= maxPos)
            {
                var b = mapped[pos];
                if (b == 0xC3 || b == 0xC2)
                    break;

                // push esi; call sub_link (0x56 0xE8 <rel32>)
                if (b == 0x56 && pos + 6 <= mapped.Length && mapped[pos + 1] == 0xE8)
                {
                    var callRel = BinaryPrimitives.ReadInt32LittleEndian(mapped.AsSpan(pos + 2, 4));
                    var subRva = pos + 6 + callRel;
                    if (subRva >= 0 && subRva < mapped.Length && visitedFunctions.Add(subRva))
                    {
                        functionsToScan.Enqueue(subRva);
                    }
                    pos += 6;
                    continue;
                }

                // push funcVa; push strVa; call esi (0x68 <funcVa:u32> 0x68 <strVa:u32> 0xFF 0xD6)
                if (b == 0x68 && pos + 12 <= mapped.Length &&
                    mapped[pos + 5] == 0x68 &&
                    mapped[pos + 10] == 0xFF && mapped[pos + 11] == 0xD6)
                {
                    var funcVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(pos + 1, 4));
                    var strVa = BinaryPrimitives.ReadUInt32LittleEndian(mapped.AsSpan(pos + 6, 4));
                    pos += 12;

                    var strRva = (int)(strVa - image.ImageBase);
                    if (strRva >= 0 && strRva < mapped.Length)
                    {
                        var end = Array.IndexOf(mapped, (byte)0, strRva);
                        if (end < 0) end = mapped.Length;
                        var declStr = Encoding.Latin1.GetString(mapped, strRva, end - strRva);

                        // Skip internal symbols whose declaration string starts with '_'
                        if (!declStr.StartsWith('_'))
                        {
                            var cmdRva = funcVa - image.ImageBase;
                            var parsed = ParseDeclaration(declStr);
                            rawCommands.Add((parsed.BasicName, cmdRva, parsed.ReturnType, parsed.ParameterTypes, parsed.DefaultValues));
                        }
                    }
                    continue;
                }

                pos++;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<Blitz3dRuntimeCommand>();

        foreach (var raw in rawCommands)
        {
            if (seen.Add(raw.BasicName))
            {
                commands.Add(new Blitz3dRuntimeCommand(
                    raw.BasicName,
                    raw.Rva,
                    raw.ReturnType,
                    raw.ParameterTypes,
                    raw.DefaultValues));
            }
        }

        return commands;
    }

    internal static (string BasicName, ExternalValueType ReturnType, IReadOnlyList<ExternalValueType> ParameterTypes, IReadOnlyList<object> DefaultValues) ParseDeclaration(string declStr)
    {
        var s = declStr.Trim();
        if (s.StartsWith('!'))
            s = s[1..].Trim();

        var retType = ExternalValueType.Void;
        var k = 0;
        if (s.Length > 0 && (s[0] == '%' || s[0] == '#' || s[0] == '$'))
        {
            retType = TagToType(s[0]);
            k = 1;
        }

        var nameStart = k;
        while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_'))
        {
            k++;
        }
        var basicName = s[nameStart..k];

        // Check if return tag is after the name, e.g. Name% or Name%(
        if (retType == ExternalValueType.Void && k < s.Length && (s[k] == '%' || s[k] == '#' || s[k] == '$'))
        {
            if (k + 1 == s.Length || s[k + 1] == '(' || s[k + 1] == ',')
            {
                retType = TagToType(s[k]);
                k++;
            }
        }

        var rest = s[k..].Trim();
        if (rest.StartsWith('(') && rest.EndsWith(')'))
            rest = rest[1..^1].Trim();

        var paramTypes = new List<ExternalValueType>();
        var defaultValues = new List<object>();

        if (rest.Length > 0)
        {
            if (rest.Contains(','))
            {
                var parts = SplitParameters(rest);
                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;

                    var eqIdx = p.IndexOf('=');
                    var hasDef = eqIdx >= 0;
                    var declPart = hasDef ? p[..eqIdx].Trim() : p;
                    var defPart = hasDef ? p[(eqIdx + 1)..].Trim() : null;

                    var pType = ExternalValueType.Integer;
                    if (declPart.Length > 0 && (declPart[0] == '%' || declPart[0] == '#' || declPart[0] == '$'))
                    {
                        pType = TagToType(declPart[0]);
                    }
                    else if (declPart.Length > 0 && (declPart[^1] == '%' || declPart[^1] == '#' || declPart[^1] == '$'))
                    {
                        pType = TagToType(declPart[^1]);
                    }

                    paramTypes.Add(pType);
                    if (defPart != null)
                    {
                        defaultValues.Add(ParseDefaultValue(pType, defPart));
                    }
                }
            }
            else
            {
                var idx = 0;
                while (idx < rest.Length)
                {
                    var pType = ExternalValueType.Integer;
                    if (rest[idx] == '%' || rest[idx] == '#' || rest[idx] == '$')
                    {
                        pType = TagToType(rest[idx]);
                        idx++;
                    }

                    var pNameStart = idx;
                    while (idx < rest.Length && (char.IsLetterOrDigit(rest[idx]) || rest[idx] == '_'))
                    {
                        idx++;
                    }

                    if (idx < rest.Length && (rest[idx] == '%' || rest[idx] == '#' || rest[idx] == '$') &&
                        (idx + 1 == rest.Length || rest[idx + 1] == '='))
                    {
                        pType = TagToType(rest[idx]);
                        idx++;
                    }

                    paramTypes.Add(pType);

                    if (idx < rest.Length && rest[idx] == '=')
                    {
                        idx++;
                        if (idx < rest.Length && rest[idx] == '\"')
                        {
                            var vStart = idx + 1;
                            idx++;
                            while (idx < rest.Length && rest[idx] != '\"')
                            {
                                idx++;
                            }
                            var valStr = rest[vStart..idx];
                            if (idx < rest.Length && rest[idx] == '\"')
                                idx++;
                            defaultValues.Add(valStr);
                        }
                        else
                        {
                            var vStart = idx;
                            if (idx < rest.Length && rest[idx] == '-')
                                idx++;
                            while (idx < rest.Length && (char.IsDigit(rest[idx]) || rest[idx] == '.'))
                            {
                                idx++;
                            }
                            var valStr = rest[vStart..idx];
                            defaultValues.Add(ParseDefaultValue(pType, valStr));
                        }
                    }
                }
            }
        }

        return (basicName, retType, paramTypes, defaultValues);
    }

    private static List<string> SplitParameters(string rest)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in rest)
        {
            if (ch == '\"')
                inQuotes = !inQuotes;

            if (ch == ',' && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static ExternalValueType TagToType(char tag) => tag switch
    {
        '%' => ExternalValueType.Integer,
        '#' => ExternalValueType.Float,
        '$' => ExternalValueType.String,
        _ => ExternalValueType.Integer
    };

    private static object ParseDefaultValue(ExternalValueType type, string raw)
    {
        raw = raw.Trim();
        switch (type)
        {
            case ExternalValueType.String:
                if (raw.Length >= 2 && raw.StartsWith('\"') && raw.EndsWith('\"'))
                    return raw[1..^1];
                return raw;

            case ExternalValueType.Integer:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iVal))
                    return iVal;
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fValAsInt))
                    return (int)fValAsInt;
                throw new FormatException($"Invalid integer default value: '{raw}'.");

            case ExternalValueType.Float:
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fVal))
                    return fVal;
                throw new FormatException($"Invalid float default value: '{raw}'.");

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported parameter type for default value.");
        }
    }
}
