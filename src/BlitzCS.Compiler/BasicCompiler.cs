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

    /// <summary>Checks BASIC semantics against declared external signatures without requiring implementations.</summary>
    public static void ValidateSemantics(string source, string sourcePath, ExternalFunctionRegistry? externals = null)
        => _ = CheckSemantics(source, sourcePath, externals);

    /// <summary>Checks semantics and returns external calls whose declared signatures lack implementations.</summary>
    public static IReadOnlyList<UnboundExternalCall> CheckSemantics(string source, string sourcePath, ExternalFunctionRegistry? externals = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var tokens = new Lexer(source, sourcePath).Tokenize();
        var syntax = new Parser(tokens, sourcePath, new IncludeResolver(sourcePath)).Parse();
        var analysis = new SemanticAnalysis(sourcePath, externals);
        _ = analysis.Analyze(syntax);
        return Array.AsReadOnly(analysis.UnboundExternalCalls.ToArray());
    }

    public static string Compile(string source, string sourcePath, ExternalFunctionRegistry? externals)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var tokens = new Lexer(source, sourcePath).Tokenize();
        var syntax = new Parser(tokens, sourcePath, new IncludeResolver(sourcePath)).Parse();
        var analysis = new SemanticAnalysis(sourcePath, externals);
        var program = analysis.Analyze(syntax);
        if (analysis.UnboundExternalCalls.FirstOrDefault() is { } unbound)
            throw new BasicDiagnosticException(unbound.SourcePath, unbound.Line, unbound.Column,
                $"External function '{unbound.Name}' has a declared signature but no implementation.");
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
