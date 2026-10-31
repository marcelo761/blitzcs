namespace BlitzCS.Compiler;

/// <summary>A syntactically discovered call to a function outside the BASIC application.</summary>
/// <param name="Name">The case-insensitive BASIC name, normalized to lowercase without its type suffix.</param>
public sealed record DependencyCallSite(string SourcePath, int Line, int Column, string Name,
    int ArgumentCount, bool UsedAsStatement);

/// <summary>AST call metadata and all parsed input paths, including Includes containing no calls.</summary>
public sealed record DependencyAnalysisResult(IReadOnlyList<DependencyCallSite> CallSites,
    IReadOnlyList<string> SourcePaths);

/// <summary>Inventories potential external calls from the parsed BASIC program and its Includes.</summary>
public static class BasicDependencyAnalysis
{
    /// <summary>
    /// Returns every external call site in source traversal order, expanding Includes in place.
    /// This is AST metadata only: it neither verifies semantics nor resolves external signatures or
    /// implementations. Syntax and Include diagnostics are propagated normally. Repeated sites remain distinct.
    /// </summary>
    public static IReadOnlyList<DependencyCallSite> Analyze(string source, string sourcePath)
        => AnalyzeDetailed(source, sourcePath).CallSites;

    /// <summary>
    /// Inventories call sites and the complete input closure without checking semantics.
    /// Source paths are absolute; Includes follow their first expansion order, even when empty.
    /// </summary>
    public static DependencyAnalysisResult AnalyzeDetailed(string source, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var includes = new IncludeResolver(sourcePath);
        var syntax = new Parser(new Lexer(source, sourcePath).Tokenize(), sourcePath, includes).Parse();
        var functions = syntax.OfType<Function>().Select(function => Primitive.Name(function.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calls = new List<DependencyCallSite>();
        VisitStatements(syntax);
        return new(Array.AsReadOnly(calls.ToArray()),
            Array.AsReadOnly(new[] { Path.GetFullPath(sourcePath) }.Concat(includes.SourcePaths).ToArray()));

        void VisitStatements(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case TypeDeclaration declaration:
                        foreach (var field in declaration.Fields)
                        {
                            VisitExpression(field.Bound);
                            VisitExpression(field.Initializer);
                        }
                        break;
                    case VectorAssignment assignment:
                        VisitExpression(assignment.Target); VisitExpression(assignment.Value); break;
                    case FieldAssignment assignment:
                        VisitExpression(assignment.Target); VisitExpression(assignment.Value); break;
                    case ArrayAssignment assignment:
                        VisitExpression(assignment.Target); VisitExpression(assignment.Value); break;
                    case Delete delete: VisitExpression(delete.Value); break;
                    case Insert insert:
                        VisitExpression(insert.Value); VisitExpression(insert.Anchor); break;
                    case ForEach loop:
                        VisitExpression(loop.Target); VisitStatements(loop.Body); break;
                    case Print print: VisitExpression(print.Value); break;
                    case Dim dim:
                        foreach (var bound in dim.Bounds) VisitExpression(bound);
                        break;
                    case Assignment assignment: VisitExpression(assignment.Value); break;
                    case ConstantDeclaration constant: VisitExpression(constant.Value); break;
                    case DataDeclaration data:
                        foreach (var value in data.Values) VisitExpression(value);
                        break;
                    case ReadData read:
                        foreach (var target in read.Targets) VisitExpression(target);
                        break;
                    case Declaration declaration:
                        VisitExpression(declaration.Bound); VisitExpression(declaration.Value); break;
                    case Function function:
                        foreach (var value in function.Defaults) VisitExpression(value);
                        VisitStatements(function.Body);
                        break;
                    case Return result: VisitExpression(result.Value); break;
                    case CallStatement call: VisitExpression(call.Value, usedAsStatement: true); break;
                    case If branch:
                        VisitExpression(branch.Condition); VisitStatements(branch.Then); VisitStatements(branch.Else); break;
                    case While loop:
                        VisitExpression(loop.Condition); VisitStatements(loop.Body); break;
                    case Repeat loop:
                        VisitStatements(loop.Body); VisitExpression(loop.Until); break;
                    case For loop:
                        VisitExpression(loop.Target); VisitExpression(loop.From); VisitExpression(loop.To);
                        VisitExpression(loop.Step); VisitStatements(loop.Body); break;
                    case Select select:
                        VisitExpression(select.Value);
                        foreach (var branch in select.Cases)
                        {
                            foreach (var value in branch.Values) VisitExpression(value);
                            VisitStatements(branch.Body);
                        }
                        VisitStatements(select.Default);
                        break;
                    case DataLabel or RestoreData or CSharp or End or Exit: break;
                    default: throw new InvalidOperationException("Unknown syntax statement.");
                }
            }
        }

        void VisitExpression(Expression? expression, bool usedAsStatement = false)
        {
            switch (expression)
            {
                case null or Literal or Variable: break;
                case Call call:
                    var name = Primitive.Name(call.Name);
                    // These are the conversion intrinsics bound directly by SemanticAnalysis.
                    // Abs/Sgn and object operations have their own AST nodes below.
                    if (!call.IsCSharp && name is not ("int" or "float" or "str") && !functions.Contains(name))
                        calls.Add(new(call.Position.File ?? sourcePath, call.Position.Line, call.Position.Column,
                            name.ToLowerInvariant(), call.Arguments.Count, usedAsStatement));
                    foreach (var argument in call.Arguments) VisitExpression(argument);
                    break;
                case ArrayAccess access:
                    foreach (var index in access.Indices) VisitExpression(index);
                    break;
                case ObjectOperation operation: VisitExpression(operation.Value); break;
                case VectorAccess access:
                    VisitExpression(access.Owner); VisitExpression(access.Index); break;
                case FieldAccess access: VisitExpression(access.Owner); break;
                case Unary unary: VisitExpression(unary.Operand); break;
                case Binary binary:
                    VisitExpression(binary.Left); VisitExpression(binary.Right); break;
                default: throw new InvalidOperationException("Unknown syntax expression.");
            }
        }
    }
}
