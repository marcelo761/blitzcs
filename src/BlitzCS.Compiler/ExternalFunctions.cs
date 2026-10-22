using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;

namespace BlitzCS.Compiler;

/// <summary>A BASIC name bound to one explicitly selected, typed .NET method.</summary>
public sealed class ExternalFunction
{
    public string Name { get; }
    public MethodInfo Method { get; }
    internal string Target { get; }
    internal ValueType Result { get; }
    internal IReadOnlyList<(ValueType Type, IrExpression? Default)> Parameters { get; }

    public ExternalFunction(string name, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (string.IsNullOrWhiteSpace(name) || !char.IsAsciiLetter(name[0]) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || Parser.IsReservedName(name))
            throw new ArgumentException("External BASIC names require a letter followed by letters, digits or underscores.", nameof(name));
        if (!method.IsPublic || !method.IsStatic || method.IsGenericMethod || method.ContainsGenericParameters ||
            method.DeclaringType is not { IsVisible: true } owner || owner.IsGenericType || owner.FullName is null ||
            string.IsNullOrEmpty(owner.Assembly.Location))
            throw new ArgumentException("External methods must be public static methods on a visible non-generic type in a file-backed assembly.", nameof(method));
        Result = Map(method.ReturnType);
        var parameters = new List<(ValueType, IrExpression?)>();
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.IsOut || parameter.ParameterType.IsByRef || parameter.GetCustomAttribute<ParamArrayAttribute>() is not null)
                throw new ArgumentException("External parameters cannot use ref, out or params.", nameof(method));
            var type = Map(parameter.ParameterType);
            IrExpression? value = null;
            if (parameter.IsOptional)
                value = parameter.DefaultValue switch
                {
                    int n when type == ValueType.Integer => new IrInteger(n),
                    float n when type == ValueType.Float && float.IsFinite(n) => new IrFloat(n),
                    string s when type == ValueType.String => new IrString(s),
                    null when type == ValueType.String => new IrString(""),
                    _ => throw new ArgumentException("External defaults must be finite typed primitive constants.", nameof(method))
                };
            parameters.Add((type, value));
        }
        Name = name.ToLowerInvariant();
        Method = method;
        Parameters = parameters.AsReadOnly();
        Target = string.Join(".", owner.FullName.Replace('+', '.').Split('.').Select(Escape)) + "." + Escape(method.Name);
    }

    private static string Escape(string identifier)
    {
        if (!SyntaxFacts.IsValidIdentifier(identifier) && SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None)
            throw new ArgumentException("External target contains a name that cannot be emitted as a C# identifier.");
        return "@" + identifier;
    }

    private static ValueType Map(Type type) => type == typeof(int) ? ValueType.Integer
        : type == typeof(float) ? ValueType.Float : type == typeof(string) ? ValueType.String
        : throw new ArgumentException($"Unsupported external type '{type}'; expected Int32, Single or String.");
}

/// <summary>Immutable, per-compilation external symbols; contains no engine implementation.</summary>
public sealed class ExternalFunctionRegistry
{
    public static ExternalFunctionRegistry Empty { get; } = new([]);
    public IReadOnlyList<ExternalFunction> Functions { get; }
    public IReadOnlyList<string> ReferencePaths { get; }
    private readonly Dictionary<string, ExternalFunction> symbols = new(StringComparer.OrdinalIgnoreCase);

    public ExternalFunctionRegistry(IEnumerable<ExternalFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        foreach (var function in functions)
        {
            ArgumentNullException.ThrowIfNull(function);
            if (!symbols.TryAdd(function.Name, function))
                throw new ArgumentException($"Duplicate external BASIC name '{function.Name}'.", nameof(functions));
        }
        Functions = Array.AsReadOnly(symbols.Values.ToArray());
        ReferencePaths = Array.AsReadOnly(symbols.Values.Select(f => f.Method.DeclaringType!.Assembly.Location).Distinct(StringComparer.Ordinal).ToArray());
    }
    internal bool Contains(string name) => symbols.ContainsKey(Primitive.Name(name));
    internal bool TryGet(string name, out ExternalFunction function) => symbols.TryGetValue(Primitive.Name(name), out function!);
}
