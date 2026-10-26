using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;

namespace BlitzCS.Compiler;

public enum ExternalValueType { Integer, Float, String, Void }
public sealed record UnboundExternalCall(string Name, string SourcePath, int Line, int Column);

/// <summary>A callable BASIC signature independent of any executable implementation.</summary>
/// <remarks>Void is permitted only as a return type for external commands.
/// Defaults apply to trailing parameters and must be Int32, finite Single or String constants
/// matching the declared types exactly. When defaults are omitted, bound .NET optional metadata is used.
/// An explicitly supplied list, including an empty list, replaces that metadata for BASIC callers.
/// Emitted calls always pass every argument; defaults do not change the bound method signature.</remarks>
public sealed class ExternalFunctionSignature
{
    public string Name { get; }
    public ExternalValueType ReturnType { get; }
    public IReadOnlyList<ExternalValueType> ParameterTypes { get; }

    /// <summary>An immutable snapshot of the explicitly declared trailing optional defaults.</summary>
    public IReadOnlyList<object> DefaultValues { get; }
    internal bool HasDeclaredDefaults { get; }
    public IReadOnlyList<bool> PointerParameters { get; }

    public ExternalFunctionSignature(string name, ExternalValueType returnType, IEnumerable<ExternalValueType> parameterTypes)
        : this(name, returnType, parameterTypes, null) { }

    public ExternalFunctionSignature(string name, ExternalValueType returnType, IEnumerable<ExternalValueType> parameterTypes,
        IEnumerable<object>? defaultValues)
        : this(name, returnType, parameterTypes, defaultValues, null) { }

    public ExternalFunctionSignature(string name, ExternalValueType returnType, IEnumerable<ExternalValueType> parameterTypes,
        IEnumerable<object>? defaultValues, IEnumerable<bool>? pointerParameters)
    {
        ArgumentNullException.ThrowIfNull(parameterTypes);
        ExternalFunction.ValidateName(name);
        if (!Enum.IsDefined(returnType)) throw new ArgumentOutOfRangeException(nameof(returnType));
        var parameters = parameterTypes.ToArray();
        if (parameters.Any(type => !Enum.IsDefined(type) || type == ExternalValueType.Void)) throw new ArgumentOutOfRangeException(nameof(parameterTypes));
        var defaults = defaultValues?.ToArray() ?? [];
        if (defaults.Length > parameters.Length)
            throw new ArgumentException("External defaults cannot exceed the parameter count.", nameof(defaultValues));
        for (var i = 0; i < defaults.Length; i++)
        {
            var valid = parameters[parameters.Length - defaults.Length + i] switch
            {
                ExternalValueType.Integer => defaults[i] is int,
                ExternalValueType.Float => defaults[i] is float value && float.IsFinite(value),
                ExternalValueType.String => defaults[i] is string,
                _ => false
            };
            if (!valid)
                throw new ArgumentException("External defaults must be finite primitive constants matching the trailing parameter types exactly.", nameof(defaultValues));
        }
        bool[] pointers;
        if (pointerParameters is null)
        {
            pointers = new bool[parameters.Length];
        }
        else
        {
            pointers = pointerParameters.ToArray();
            if (pointers.Length != parameters.Length)
                throw new ArgumentException("Pointer parameter flags must match parameter count.", nameof(pointerParameters));
            for (var i = 0; i < pointers.Length; i++)
            {
                if (pointers[i] && parameters[i] != ExternalValueType.Integer)
                    throw new ArgumentException("Pointer parameters must be declared as Integer type.", nameof(pointerParameters));
            }
        }
        PointerParameters = Array.AsReadOnly(pointers);
        HasDeclaredDefaults = defaultValues is not null;
        DefaultValues = Array.AsReadOnly(defaults);
        Name = name.ToLowerInvariant();
        ReturnType = returnType;
        ParameterTypes = Array.AsReadOnly(parameters);
    }
}

/// <summary>A BASIC name bound to one explicitly selected, typed .NET method.</summary>
public sealed class ExternalFunction
{
    public string Name { get; }
    public MethodInfo Method { get; }
    internal string Target { get; }
    internal ValueType Result { get; }
    internal IReadOnlyList<(ValueType Type, IrExpression? Default)> Parameters { get; }
    public ExternalFunctionSignature Signature { get; }

    public ExternalFunction(string name, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        ValidateName(name);
        if (!method.IsPublic || !method.IsStatic || method.IsGenericMethod || method.ContainsGenericParameters ||
            method.DeclaringType is not { IsVisible: true } owner || owner.IsGenericType || owner.FullName is null ||
            string.IsNullOrEmpty(owner.Assembly.Location))
            throw new ArgumentException("External methods must be public static methods on a visible non-generic type in a file-backed assembly.", nameof(method));
        Result = method.ReturnType == typeof(void) ? ValueType.Void : Map(method.ReturnType);
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
        Signature = new(Name, ToExternal(Result), Parameters.Select(parameter => ToExternal(parameter.Type)));
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !char.IsAsciiLetter(name[0]) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || Parser.IsReservedName(name))
            throw new ArgumentException("External BASIC names require a letter followed by letters, digits or underscores.", nameof(name));
    }

    private static ExternalValueType ToExternal(ValueType type) => type == ValueType.Void ? ExternalValueType.Void
        : type == ValueType.Integer ? ExternalValueType.Integer
        : type == ValueType.Float ? ExternalValueType.Float : ExternalValueType.String;
    internal static ValueType ToInternal(ExternalValueType type) => type switch
    {
        ExternalValueType.Integer => ValueType.Integer, ExternalValueType.Float => ValueType.Float,
        ExternalValueType.String => ValueType.String, ExternalValueType.Void => ValueType.Void, _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

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
    public static ExternalFunctionRegistry Empty { get; } = new(Array.Empty<ExternalFunction>());
    public IReadOnlyList<ExternalFunction> Functions { get; private set; }
    public IReadOnlyList<string> ReferencePaths { get; private set; }
    private readonly Dictionary<string, ExternalFunction> bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExternalFunctionSignature> symbols = new(StringComparer.OrdinalIgnoreCase);

    public ExternalFunctionRegistry(IEnumerable<ExternalFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        foreach (var function in functions)
        {
            ArgumentNullException.ThrowIfNull(function);
            if (!symbols.TryAdd(function.Name, function.Signature))
                throw new ArgumentException($"Duplicate external BASIC name '{function.Name}'.", nameof(functions));
            bindings.Add(function.Name, function);
        }
        Functions = Array.AsReadOnly(bindings.Values.ToArray());
        ReferencePaths = Array.AsReadOnly(bindings.Values.Select(f => f.Method.DeclaringType!.Assembly.Location).Distinct(StringComparer.Ordinal).ToArray());
    }
    private ExternalFunctionRegistry(IEnumerable<ExternalFunctionSignature> signatures)
    {
        foreach (var signature in signatures)
        {
            ArgumentNullException.ThrowIfNull(signature);
            if (!symbols.TryAdd(signature.Name, signature))
                throw new ArgumentException($"Duplicate external BASIC name '{signature.Name}'.", nameof(signatures));
        }
        Functions = Array.AsReadOnly(Array.Empty<ExternalFunction>());
        ReferencePaths = Array.AsReadOnly(Array.Empty<string>());
    }
    public static ExternalFunctionRegistry FromSignatures(IEnumerable<ExternalFunctionSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        return new ExternalFunctionRegistry(signatures);
    }
    public static ExternalFunctionRegistry FromDeclarations(IEnumerable<ExternalFunctionSignature> signatures, IEnumerable<ExternalFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(functions);
        var registry = FromSignatures(signatures);
        foreach (var function in functions)
        {
            ArgumentNullException.ThrowIfNull(function);
            if (!registry.symbols.TryGetValue(function.Name, out var signature) || !SameSignature(signature, function.Signature))
                throw new ArgumentException($"External implementation '{function.Name}' does not match a declared signature.", nameof(functions));
            if (!registry.bindings.TryAdd(function.Name, function))
                throw new ArgumentException($"Duplicate external implementation '{function.Name}'.", nameof(functions));
        }
        registry.Functions = Array.AsReadOnly(registry.bindings.Values.ToArray());
        registry.ReferencePaths = Array.AsReadOnly(registry.bindings.Values.Select(f => f.Method.DeclaringType!.Assembly.Location).Distinct(StringComparer.Ordinal).ToArray());
        return registry;
    }
    private static bool SameSignature(ExternalFunctionSignature left, ExternalFunctionSignature right) =>
        left.ReturnType == right.ReturnType && left.ParameterTypes.SequenceEqual(right.ParameterTypes);
    internal bool Contains(string name) => symbols.ContainsKey(Primitive.Name(name));
    internal bool TryGetSignature(string name, out ExternalFunctionSignature signature) => symbols.TryGetValue(Primitive.Name(name), out signature!);
    internal bool TryGetBinding(string name, out ExternalFunction function) => bindings.TryGetValue(Primitive.Name(name), out function!);
}
