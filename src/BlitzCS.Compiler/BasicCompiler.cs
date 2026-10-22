namespace BlitzCS.Compiler;

/// <summary>Compiles the documented console subset of BlitzBasic into standalone C# source.</summary>
public static class BasicCompiler
{
    public static string Compile(string source, string sourcePath) => Compile(source, sourcePath, null);

    /// <summary>Checks lexical and parser syntax only; successful validation does not imply semantic validity.</summary>
    public static void ValidateSyntax(string source, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var tokens = new Lexer(source, sourcePath).Tokenize();
        _ = new Parser(tokens, sourcePath, new IncludeResolver(sourcePath)).Parse();
    }

    public static string Compile(string source, string sourcePath, ExternalFunctionRegistry? externals)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var tokens = new Lexer(source, sourcePath).Tokenize();
        var syntax = new Parser(tokens, sourcePath, new IncludeResolver(sourcePath)).Parse();
        var program = new SemanticAnalysis(sourcePath, externals).Analyze(syntax);
        return CSharpEmitter.Emit(program, sourcePath);
    }
}

public sealed class BasicDiagnosticException : Exception
{
    public string SourcePath { get; }
    public int Line { get; }
    public int Column { get; }

    public BasicDiagnosticException(string sourcePath, int line, int column, string message)
        : base($"{sourcePath}({line},{column}): {message}")
    {
        SourcePath = sourcePath;
        Line = line;
        Column = column;
    }
}
