using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BlitzCS.Compiler;

// Compile-time symbol binding only: module code is never loaded or executed here.
internal sealed class CSharpBridge
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The host runtime does not expose platform references."))
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray());
    private readonly CSharpCompilation compilation;
    private readonly string sourcePath;

    internal CSharpBridge(IReadOnlyList<Statement> statements, string sourcePath, ExternalFunctionRegistry? externals = null)
    {
        this.sourcePath = sourcePath;
        var source = new StringBuilder();
        foreach (var module in statements.OfType<CSharp>().Where(c => c.IsModule)) source.AppendLine(module.Code);
        source.AppendLine("public static class BB {");
        foreach (var global in statements.OfType<Declaration>().Where(d => d.IsGlobal && d.Bound is null && !Primitive.Type(d.Name).IsObject))
            source.AppendLine($"public static {Primitive.CSharp(Primitive.Type(global.Name))} @{Primitive.Name(global.Name)};");
        foreach (var function in statements.OfType<Function>().Where(f => !Primitive.Type(f.Name).IsObject && f.Parameters.All(p => !Primitive.Type(p).IsObject)))
            source.AppendLine($"public static {Primitive.CSharp(Primitive.Type(function.Name))} @{Primitive.Name(function.Name)}({string.Join(",", function.Parameters.Select((p,i) => $"{Primitive.CSharp(Primitive.Type(p))} v{i}"))}) => default;");
        source.AppendLine("}");
        compilation = CSharpCompilation.Create("BlitzBridgeProbe", [CSharpSyntaxTree.ParseText(source.ToString(), new CSharpParseOptions(LanguageVersion.CSharp14))], References.Value.Concat((externals?.ReferencePaths ?? []).Select(path => MetadataReference.CreateFromFile(path))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    internal IrCall Bind(Call call, IReadOnlyList<IrExpression> arguments)
    {
        BasicDiagnosticException Error(string message) => new(call.Position.File ?? sourcePath, call.Position.Line, call.Position.Column, message);
        if (arguments.Any(a => a.Type.IsObject || a.Type.IsVector)) throw Error("Object references cannot cross the C# primitive bridge.");
        // Typed defaults deliberately prevent C# constant-expression narrowing and null overload ambiguity.
        var source = $"internal static class __BlitzProbe {{ static void Probe() {{ global::{call.Name}({string.Join(",", arguments.Select(a => $"default({Primitive.CSharp(a.Type)})"))}); }} }}";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        var probe = compilation.AddSyntaxTrees(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var model = probe.GetSemanticModel(tree);
        var within = model.GetEnclosingSymbol(invocation.SpanStart)!.ContainingType;
        var candidates = model.GetMemberGroup(invocation.Expression).OfType<IMethodSymbol>()
            .Where(method => method.IsStatic && !method.IsGenericMethod &&
                probe.IsSymbolAccessibleWithin(method, within) &&
                method.Parameters.Length == arguments.Count &&
                method.Parameters.All(p => p.RefKind == RefKind.None && !p.IsParams))
            .Select(method => (Method: method, Costs: method.Parameters.Select((p, i) =>
            {
                var type = Map(p.Type);
                return type == arguments[i].Type ? 0
                    : arguments[i].Type == ValueType.Integer && type == ValueType.Float ? 1 : 2;
            }).ToArray()))
            .Where(candidate => candidate.Costs.All(cost => cost < 2)).ToArray();
        // An overload wins only if it is no worse in every argument and better in one.
        // Summing promotions would incorrectly pick a winner for incomparable signatures.
        var best = candidates.Where(candidate => !candidates.Any(other =>
            other.Costs.Zip(candidate.Costs).All(pair => pair.First <= pair.Second) &&
            other.Costs.Zip(candidate.Costs).Any(pair => pair.First < pair.Second))).ToArray();
        if (best.Length > 1) throw Error($"Ambiguous C# static call '{call.Name}' for the supplied primitive argument types.");
        if (best.Length == 0)
        {
            var detail = string.Join(" ", model.GetDiagnostics(invocation.Span).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage()));
            throw Error($"Cannot resolve C# static call '{call.Name}'. Expected an accessible non-generic static method with exact Integer/Float/String arguments or Integer-to-Float promotion; use Int, Float or Str explicitly. {detail}");
        }
        var method = best[0].Method;
        var converted = new IrExpression[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var type = Map(method.Parameters[i].Type);
            if (type is null || type != arguments[i].Type && !(arguments[i].Type == ValueType.Integer && type == ValueType.Float))
                throw Error($"C# call '{call.Name}' accepts exact Integer/Float/String arguments or Integer-to-Float promotion; use Int, Float or Str explicitly.");
            converted[i] = arguments[i].Type == type.Value ? arguments[i] : new IrConvert(arguments[i], type.Value);
        }
        var result = Map(method.ReturnType);
        // Preserve the earlier bridge's safe integral widening without accepting double/bool/object.
        if (method.ReturnType.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char)
            result = ValueType.Integer;
        if (result is null) throw Error($"C# call '{call.Name}' returns unsupported type '{method.ReturnType.ToDisplayString()}'; expected int, float or string.");
        return new(call.Name, converted, true, result.Value);
    }

    private static ValueType? Map(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Int32 => ValueType.Integer,
        SpecialType.System_Single => ValueType.Float,
        SpecialType.System_String => ValueType.String,
        _ => null
    };
}
