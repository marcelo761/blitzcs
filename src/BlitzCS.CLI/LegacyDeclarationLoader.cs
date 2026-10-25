using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BlitzCS.CLI.Tests")]

namespace BlitzCS.CLI;

/// <summary>Reads legacy userlib contracts without loading libraries or creating executable bindings.</summary>
internal static class LegacyDeclarationLoader
{
    internal static IReadOnlyList<JsonObject> Read(string path)
    {
        path = Path.GetFullPath(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var rows = new List<JsonObject>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var library = "";
        var lines = File.ReadAllLines(path, Encoding.GetEncoding(1252));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = index + 1;
            ArgumentException Error(string message) => new($"{path}({line}): {message}");
            var parser = new LineParser(lines[index], Error);
            if (parser.End) continue;
            if (parser.Take('.'))
            {
                if (!parser.Identifier().Equals("lib", StringComparison.OrdinalIgnoreCase))
                    throw Error("Unsupported declaration directive; expected .lib.");
                library = parser.String();
                parser.Finish();
                continue;
            }
            var name = parser.Identifier();
            var returnType = Type(parser.Marker(), "Void");
            parser.Expect('(');
            var parameters = new JsonArray();
            var metadata = new JsonArray();
            var defaults = new JsonArray();
            var optional = false;
            if (!parser.Take(')'))
            {
                do
                {
                    var start = parser.Position;
                    var argument = parser.Identifier();
                    var basicType = Type(parser.Marker(), "Integer");
                    var pointer = parser.Take('*');
                    parameters.Add(pointer ? "Integer" : basicType);
                    if (parser.Take('='))
                    {
                        if (pointer) throw Error("Pointer defaults require an explicit ABI contract and are unsupported.");
                        optional = true;
                        defaults.Add(parser.Default(basicType));
                    }
                    else if (optional) throw Error("Optional parameters must be trailing.");
                    metadata.Add(new JsonObject
                    {
                        ["name"] = argument, ["basicType"] = basicType, ["isPointer"] = pointer,
                        ["raw"] = parser.Slice(start)
                    });
                } while (parser.Take(','));
                parser.Expect(')');
            }
            var export = parser.Take(':') ? parser.String() : name;
            if (export.Length == 0) throw Error("Export alias cannot be empty.");
            parser.Finish();
            if (names.TryGetValue(name, out var previous))
                throw Error($"Duplicate BASIC declaration '{name}'; first declared at {path}({previous}).");
            names.Add(name, line);
            rows.Add(new JsonObject
            {
                ["name"] = name, ["returnType"] = returnType, ["parameters"] = parameters, ["defaults"] = defaults,
                ["legacyDeclaration"] = new JsonObject
                {
                    ["source"] = path, ["line"] = line, ["rawSignature"] = lines[index].Trim(),
                    ["library"] = library, ["export"] = export, ["callingConvention"] = "stdcall",
                    ["parameters"] = metadata, ["returnType"] = returnType
                }
            });
        }
        return rows.AsReadOnly();
    }

    private static string Type(char marker, string fallback) => marker switch
    {
        '%' => "Integer", '#' => "Float", '$' => "String", _ => fallback
    };

    private sealed class LineParser(string text, Func<string, ArgumentException> error)
    {
        private int position;
        internal int Position { get { Space(); return position; } }
        internal bool End { get { Space(); return position == text.Length || text[position] == ';'; } }
        private void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        internal bool Take(char value)
        {
            Space();
            if (position >= text.Length || text[position] != value) return false;
            position++;
            return true;
        }
        internal void Expect(char value)
        {
            if (!Take(value)) throw error($"Expected '{value}' at column {position + 1}.");
        }
        internal void Finish()
        {
            if (!End) throw error($"Unsupported trailing syntax at column {position + 1}.");
        }
        internal string Identifier()
        {
            Space();
            var start = position;
            if (position >= text.Length || !(char.IsAsciiLetter(text[position]) || text[position] == '_'))
                throw error($"Expected identifier at column {position + 1}.");
            position++;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) position++;
            return text[start..position];
        }
        internal char Marker()
        {
            Space();
            return position < text.Length && text[position] is '%' or '#' or '$' ? text[position++] : '\0';
        }
        internal string String()
        {
            Expect('"');
            var result = new StringBuilder();
            while (position < text.Length)
            {
                var value = text[position++];
                if (value != '"') { result.Append(value); continue; }
                if (position < text.Length && text[position] == '"') { result.Append('"'); position++; continue; }
                return result.ToString();
            }
            throw error("Unterminated quoted string.");
        }
        internal JsonNode Default(string type)
        {
            if (type == "String") return JsonValue.Create(String())!;
            Space();
            var start = position;
            while (position < text.Length && text[position] is not (',' or ')' or ';')) position++;
            var token = text[start..position].Trim();
            if (type == "Integer" && int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                return JsonValue.Create(integer)!;
            if (type == "Float" && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number))
                return JsonValue.Create(number)!;
            throw error($"Unsupported {type} default '{token}'; expected a finite typed constant.");
        }
        internal string Slice(int start) => text[start..position].Trim();
    }
}
