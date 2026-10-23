namespace BlitzCS.Compiler;

// The original CLI sets CWD to the main file's directory before parsing.
// Keep that base per compilation instead of mutating the host's process CWD.
internal sealed class IncludeResolver(string mainPath)
{
    private readonly string directory = Path.GetDirectoryName(Path.GetFullPath(mainPath))!;
    private readonly List<string> sourcePaths = [];
    internal IReadOnlyList<string> SourcePaths => sourcePaths.AsReadOnly();
    private readonly HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<Token>? Load(Token directive, Token filename)
    {
        try
        {
            var name = filename.Text.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(name, directory);
            // Main is deliberately not pre-seeded: a cycle may include it once.
            if (!included.Add(path)) return null;
            path = FindFile(path);
            sourcePaths.Add(path);
            return new Lexer(File.ReadAllText(path), path).Tokenize();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new BasicDiagnosticException(directive.Position.File ?? mainPath,
                directive.Position.Line, directive.Position.Column, $"Unable to open include file '{filename.Text}': {error.Message}");
        }
    }

    private static string FindFile(string path)
    {
        if (File.Exists(path)) return path;
        // Legacy projects use Windows casing even on a case-sensitive host.
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = Directory.EnumerateFileSystemEntries(current)
                .Where(entry => string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 0) throw new FileNotFoundException("File or directory not found.", path);
            if (matches.Length > 1) throw new IOException($"Ambiguous case-insensitive include path '{path}'.");
            current = matches[0];
        }
        return current;
    }
}
