using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlitzCS.BinaryCompiler;
using BlitzCS.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BlitzCS.CLI;

internal sealed record UserLibFunctionDeclaration(
    string BasicName,
    string ExportName,
    string LibraryPath,
    ExternalValueType ReturnType,
    IReadOnlyList<ExternalValueType> ParameterTypes,
    string SourceDeclsPath,
    int Line);

internal static partial class UserLibResolver
{
    [GeneratedRegex(@"^\.lib\s+""([^""]*)""\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibDirectiveRegex();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)([%#$]?)\s*\(([^)]*)\)\s*(?::\s*""([^""]+)"")?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionDeclRegex();

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)([%#$*])\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterDeclRegex();

    internal static IReadOnlyList<UserLibFunctionDeclaration> ParseDeclsFile(string declsPath, string fallbackLibraryDirectory)
    {
        declsPath = Path.GetFullPath(declsPath);
        if (!File.Exists(declsPath))
            throw new FileNotFoundException($"Userlib declaration file '{Path.GetFileName(declsPath)}' does not exist.", declsPath);

        var declsDirectory = Path.GetDirectoryName(declsPath)!;
        var results = new List<UserLibFunctionDeclaration>();
        string? currentLibraryPath = null;
        var ignoreCurrentLib = false;

        var lines = File.ReadAllLines(declsPath, Encoding.Latin1);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
                continue;

            var libMatch = LibDirectiveRegex().Match(line);
            if (libMatch.Success)
            {
                var libName = libMatch.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(libName))
                {
                    ignoreCurrentLib = true;
                    currentLibraryPath = null;
                    continue;
                }

                ignoreCurrentLib = false;
                var candidateInDeclsDir = Path.GetFullPath(libName, declsDirectory);
                var candidateInCatalogDir = Path.GetFullPath(libName, fallbackLibraryDirectory);
                currentLibraryPath = File.Exists(candidateInDeclsDir) ? candidateInDeclsDir : candidateInCatalogDir;
                continue;
            }

            if (ignoreCurrentLib)
                continue;

            var fnMatch = FunctionDeclRegex().Match(line);
            if (!fnMatch.Success)
                throw new ArgumentException($"{declsPath}({i + 1}): invalid .decls declaration '{line}'.");

            if (currentLibraryPath is null)
                throw new ArgumentException($"{declsPath}({i + 1}): declaration '{fnMatch.Groups[1].Value}' appears before a .lib directive.");

            var basicName = fnMatch.Groups[1].Value;
            var returnTag = fnMatch.Groups[2].Value;
            var rawParams = fnMatch.Groups[3].Value;
            var exportSpec = fnMatch.Groups[4].Success ? fnMatch.Groups[4].Value : basicName;

            var returnType = returnTag switch
            {
                "%" => ExternalValueType.Integer,
                "#" => ExternalValueType.Float,
                "$" => ExternalValueType.String,
                "" => ExternalValueType.Void,
                _ => throw new ArgumentException($"{declsPath}({i + 1}): unsupported return type tag '{returnTag}' on '{basicName}'.")
            };

            var paramTypes = new List<ExternalValueType>();
            if (!string.IsNullOrWhiteSpace(rawParams))
            {
                foreach (var part in rawParams.Split(','))
                {
                    var paramMatch = ParameterDeclRegex().Match(part);
                    if (!paramMatch.Success)
                        throw new ArgumentException($"{declsPath}({i + 1}): unsupported parameter '{part.Trim()}' in '{basicName}'.");

                    var paramTag = paramMatch.Groups[2].Value;
                    if (paramTag == "*")
                        throw new ArgumentException($"{declsPath}({i + 1}): pointer parameter '{part.Trim()}' in '{basicName}' requires an explicit ABI bridge.");

                    paramTypes.Add(paramTag switch
                    {
                        "%" => ExternalValueType.Integer,
                        "#" => ExternalValueType.Float,
                        "$" => ExternalValueType.String,
                        _ => throw new ArgumentException($"{declsPath}({i + 1}): unsupported parameter tag '{paramTag}' in '{basicName}'.")
                    });
                }
            }

            results.Add(new UserLibFunctionDeclaration(
                basicName,
                exportSpec,
                currentLibraryPath,
                returnType,
                paramTypes,
                declsPath,
                i + 1));
        }

        return results;
    }

    internal static string GenerateDirectNativeBindings(
        IReadOnlyList<UserLibFunctionDeclaration> declarations,
        string stagingDirectory)
    {
        var source = new StringBuilder();
        source.AppendLine("using System.Runtime.InteropServices;");
        source.AppendLine("namespace BlitzCS.NativeUserLibBindings;");
        source.AppendLine("public static class DirectUserLibs");
        source.AppendLine("{");

        for (var i = 0; i < declarations.Count; i++)
        {
            var decl = declarations[i];
            var dllName = Escape(Path.GetFileName(decl.LibraryPath));
            var entryPoint = Escape(decl.ExportName);
            var convention = decl.ExportName.StartsWith('_') && decl.ExportName.Contains('@')
                ? "CallingConvention.StdCall"
                : "CallingConvention.Winapi";
            var returnType = CsType(decl.ReturnType);
            var paramList = string.Join(", ", decl.ParameterTypes.Select((t, idx) =>
                t == ExternalValueType.String
                    ? $"[MarshalAs(UnmanagedType.LPStr)] string a{idx}"
                    : $"{CsType(t)} a{idx}"));
            var argList = string.Join(", ", decl.ParameterTypes.Select((_, idx) => $"a{idx}"));

            source.AppendLine($"    [DllImport(\"{dllName}\", EntryPoint = \"{entryPoint}\", CallingConvention = {convention}, ExactSpelling = false, CharSet = CharSet.Ansi)]");
            if (decl.ReturnType == ExternalValueType.String)
                source.AppendLine("    [return: MarshalAs(UnmanagedType.LPStr)]");
            source.AppendLine($"    private static extern {returnType} PInvoke_{i}({paramList});");
            source.AppendLine($"    public static {returnType} {BinaryBindingGenerator.MethodName(decl.BasicName)}({ string.Join(", ", decl.ParameterTypes.Select((t, idx) => $"{CsType(t)} a{idx}")) }) => PInvoke_{i}({argList});");
        }

        source.AppendLine("}");

        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToList();

        var syntaxTree = CSharpSyntaxTree.ParseText(source.ToString(), new CSharpParseOptions(LanguageVersion.CSharp12));
        var compilation = CSharpCompilation.Create(
            "BlitzCS.NativeUserLibBindings",
            [syntaxTree],
            trusted,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        var output = Path.Combine(stagingDirectory, "BlitzCS.NativeUserLibBindings.dll");
        var emit = compilation.Emit(output);
        if (!emit.Success)
        {
            var message = string.Join(Environment.NewLine, emit.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
            throw new InvalidOperationException("Direct native userlib binding generation failed:" + Environment.NewLine + message);
        }

        return output;
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes)
                return line[..i];
        }
        return line;
    }

    private static string CsType(ExternalValueType type) => type switch
    {
        ExternalValueType.Integer => "int",
        ExternalValueType.Float => "float",
        ExternalValueType.String => "string",
        ExternalValueType.Void => "void",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
