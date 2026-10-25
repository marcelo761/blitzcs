namespace BlitzCS.Compiler;

internal readonly record struct ValueType(string Name, bool IsObject = false, int VectorLength = 0, bool ObjectElements = false)
{
    internal static readonly ValueType Integer = new("Integer"), Float = new("Float"), String = new("String"), Void = new("Void"), Null = new("Null", true);
    internal static ValueType Object(string name) => new(name, true);
    internal bool IsVector => VectorLength > 0;
    internal ValueType ElementType => new(Name, ObjectElements);
    internal static ValueType Vector(ValueType element, int length) => new(element.Name, false, length, element.IsObject);
    public override string ToString() => IsVector ? $"{ElementType}[{VectorLength - 1}]" : Name;
}
internal static class Primitive
{
    internal static string Name(string name) => name.Split('.')[0].TrimEnd('%', '#', '$');
    internal static ValueType Type(string name) => name.Contains('.') ? ValueType.Object(name[(name.IndexOf('.') + 1)..]) : name.EndsWith('#') ? ValueType.Float : name.EndsWith('$') ? ValueType.String : ValueType.Integer;
    internal static bool Tagged(string name) => name.Contains('.') || name[^1] is '%' or '#' or '$';
    internal static string CSharp(ValueType type) => type.IsVector ? $"global::__BlitzVector<{CSharp(type.ElementType)}>" : type.IsObject ? "global::__BlitzObject_" + type.Name : type == ValueType.Float ? "float" : type == ValueType.String ? "string" : "int";
    internal static string Default(ValueType type) => type.IsVector ? $"new {CSharp(type)}({type.VectorLength})" : type.IsObject ? "null" : type == ValueType.Float ? "0f" : type == ValueType.String ? "\"\"" : "0";
}
internal abstract record IrExpression(ValueType Type);
internal sealed record IrObjectOperation(string Operation, ValueType ObjectType, IrExpression? Value, string Location) : IrExpression(ObjectType);
internal sealed record IrVectorAccess(IrExpression Owner, IrExpression Index, string Location) : IrExpression(Owner.Type.ElementType);
internal sealed record IrField(IrExpression Owner, string Name, ValueType FieldType, string Location) : IrExpression(FieldType);
internal sealed record IrInteger(int Value) : IrExpression(ValueType.Integer);
internal sealed record IrFloat(float Value) : IrExpression(ValueType.Float);
internal sealed record IrString(string Value) : IrExpression(ValueType.String);
internal sealed record IrVariable(string Storage, ValueType ValueType) : IrExpression(ValueType);
internal sealed record IrArrayAccess(string Storage, IReadOnlyList<IrExpression> Indices, ValueType ElementType, string Location) : IrExpression(ElementType);
internal sealed record IrDataRead(IrExpression Target, string Location, SourcePosition At) : IrStatement(At);
internal sealed record IrDataRestore(int Index, SourcePosition At) : IrStatement(At);
internal sealed record IrDataValue(ValueType Type, int Integer, float Float, string? Text);
internal sealed record IrConvert(IrExpression Value, ValueType Target) : IrExpression(Target);
internal sealed record IrCall(string Name, IReadOnlyList<IrExpression> Arguments, bool IsCSharp, ValueType Result) : IrExpression(Result);
internal sealed record IrUnary(TokenKind Operator, IrExpression Operand) : IrExpression(Operand.Type);
internal static class Operators
{
    internal static bool IsRelation(TokenKind op) => op is TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.Greater or TokenKind.LessEqual or TokenKind.GreaterEqual;
    internal static bool IsBitwise(TokenKind op) => op is TokenKind.And or TokenKind.Or or TokenKind.Xor or TokenKind.Shl or TokenKind.Shr or TokenKind.Sar;
}
internal sealed record IrBinary(IrExpression Left, TokenKind Operator, IrExpression Right) : IrExpression(Operators.IsRelation(Operator) ? ValueType.Integer : Left.Type);
internal abstract record IrStatement(SourcePosition Position);
internal sealed record IrVectorAssign(IrVectorAccess Target, IrExpression Value, SourcePosition At) : IrStatement(At);
internal sealed record IrFieldAssign(IrField Target, IrExpression Value, SourcePosition At) : IrStatement(At);
internal sealed record IrDelete(IrExpression? Value, ValueType ObjectType, SourcePosition At) : IrStatement(At);
internal sealed record IrInsert(IrExpression Value, IrExpression Anchor, bool Before, string Location, SourcePosition At) : IrStatement(At);
internal sealed record IrForEach(IrExpression Target, ValueType ObjectType, IReadOnlyList<IrStatement> Body, SourcePosition At) : IrStatement(At);
internal sealed record IrObjectType(string Name, IReadOnlyDictionary<string, ValueType> Fields);
internal sealed record IrPrint(IrExpression Value, SourcePosition At) : IrStatement(At);
internal sealed record IrArrayAssign(IrArrayAccess Target, IrExpression Value, SourcePosition At) : IrStatement(At);
internal sealed record IrDim(IrArray Array, IReadOnlyList<IrExpression> Bounds, string Location, SourcePosition At) : IrStatement(At);
internal sealed record IrAssign(string Storage, IrExpression Value, SourcePosition At) : IrStatement(At);
internal sealed record IrReturn(IrExpression Value, SourcePosition At) : IrStatement(At);
internal sealed record IrCSharp(string Code, SourcePosition At) : IrStatement(At);
internal sealed record IrCallStatement(IrCall Value, SourcePosition At) : IrStatement(At);
internal sealed record IrEnd(SourcePosition At) : IrStatement(At);
internal sealed record IrIf(IrExpression Condition, IReadOnlyList<IrStatement> Then, IReadOnlyList<IrStatement> Else, SourcePosition At) : IrStatement(At);
internal sealed record IrWhile(IrExpression Condition, IReadOnlyList<IrStatement> Body, SourcePosition At) : IrStatement(At);
internal sealed record IrRepeat(IReadOnlyList<IrStatement> Body, IrExpression? Until, SourcePosition At) : IrStatement(At);
internal sealed record IrFor(IrExpression Variable, IrExpression From, IrExpression To, IrExpression Step, bool Ascending, IReadOnlyList<IrStatement> Body, SourcePosition At) : IrStatement(At);
internal sealed record IrCase(IReadOnlyList<IrExpression> Values, IReadOnlyList<IrStatement> Body, SourcePosition Position);
internal sealed record IrSelect(IrExpression Value, IReadOnlyList<IrCase> Cases, IReadOnlyList<IrStatement> Default, SourcePosition At) : IrStatement(At);
internal sealed record IrExit(SourcePosition At) : IrStatement(At);
internal sealed record IrScope(IReadOnlyList<ValueType> Variables, int ParameterCount, IReadOnlyList<IrStatement> Statements, ValueType ReturnType);
internal sealed record IrFunction(string Name, ValueType ReturnType, IrScope Scope, SourcePosition Position);
internal sealed record IrGlobal(string Name, ValueType Type);
internal sealed record IrArray(string Name, string Storage, ValueType Type, int Rank);
internal sealed record IrProgram(IrScope Main, IReadOnlyList<IrArray> Arrays, IReadOnlyList<IrGlobal> Globals, IReadOnlyList<IrFunction> Functions, IReadOnlyList<CSharp> Modules, IReadOnlyList<IrObjectType> ObjectTypes, IReadOnlyList<IrDataValue> Data, bool UsesData);

internal sealed partial class SemanticAnalysis(string sourcePath, ExternalFunctionRegistry? externals = null)
{
    internal List<UnboundExternalCall> UnboundExternalCalls { get; } = [];
    private readonly Dictionary<string, IrObjectType> objectTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IrArray> arrays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Function> functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<IrExpression?>> functionDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ValueType> globals = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (int Slot, ValueType Type)> locals = new(StringComparer.OrdinalIgnoreCase);
    private CSharpBridge bridge = null!;
    private readonly Dictionary<string, IrExpression> constants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> dataLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IrDataValue> data = [];
    private bool usesData;
    private bool bindingConstant;
    private BasicDiagnosticException Error(SourcePosition at, string message) => new(at.File ?? sourcePath, at.Line, at.Column, message);

    private void CheckTag(string name, ValueType type, SourcePosition at)
    {
        if (Primitive.Tagged(name) && Primitive.Type(name) != type)
            throw Error(at, $"Type suffix for '{Primitive.Name(name)}' does not match its declared {type} type.");
    }
    private IrVariable Slot(string name, SourcePosition at)
    {
        var key = Primitive.Name(name);
        if (locals.TryGetValue(key, out var local))
        {
            CheckTag(name, local.Type.IsVector ? local.Type.ElementType : local.Type, at);
            return new($"v{local.Slot}", local.Type);
        }
        if (constants.ContainsKey(key)) throw Error(at, $"Constant '{key}' cannot be assigned to.");
        if (globals.TryGetValue(key, out var type))
        {
            CheckTag(name, type.IsVector ? type.ElementType : type, at);
            return new($"global::BB.@{key}", type);
        }
        if (arrays.ContainsKey(key)) throw Error(at, $"Array '{key}' requires indices.");
        type = DeclaredType(name, at);
        locals.Add(key, (locals.Count, type));
        return new($"v{locals.Count - 1}", type);
    }

    internal IrProgram Analyze(IReadOnlyList<Statement> statements)
    {
        bindingConstant = true;
        foreach (var declaration in statements.OfType<ConstantDeclaration>())
        {
            var key = Primitive.Name(declaration.Name);
            if (constants.ContainsKey(key)) throw Error(declaration.Position, $"Duplicate constant '{key}'.");
            if (externals?.Contains(key) == true) throw Error(declaration.Position, $"Constant '{key}' conflicts with a registered external function.");
            var type = Primitive.Type(declaration.Name);
            if (type.IsObject) throw Error(declaration.Position, "Const requires Integer, Float or String.");
            var value = Convert(Bind(declaration.Value), type, declaration.Position);
            IrExpression folded;
            if (type == ValueType.String && ConstantText(value) is string text) folded = new IrString(text);
            else if (type != ValueType.String && Constant(value) is double number)
                folded = type == ValueType.Float ? new IrFloat((float)number) : new IrInteger(unchecked((int)number));
            else throw Error(declaration.Position, "Const initializer must be constant.");
            constants.Add(key, folded);
        }
        bindingConstant = false;
        CollectData(statements);
        foreach (var declaration in statements.OfType<TypeDeclaration>())
        {
            var fields = new Dictionary<string, ValueType>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in declaration.Fields)
                if (!fields.TryAdd(Primitive.Name(field.Name), Primitive.Type(field.Name))) throw Error(declaration.Position, $"Duplicate field '{field.Name}'.");
            if (!objectTypes.TryAdd(declaration.Name, new(declaration.Name, fields))) throw Error(declaration.Position, $"Duplicate Type '{declaration.Name}'.");
        }
        foreach (var declaration in statements.OfType<TypeDeclaration>())
        {
            var fields = (Dictionary<string, ValueType>)objectTypes[declaration.Name].Fields;
            foreach (var field in declaration.Fields)
            {
                var type = DeclaredType(field.Name, field.Position);
                fields[Primitive.Name(field.Name)] = field.Bound is null ? type : VectorType(type, field.Bound);
            }
        }
        foreach (var function in statements.OfType<Function>())
            if (constants.ContainsKey(Primitive.Name(function.Name)) || externals?.ConflictsWithApplicationFunction(function.Name) == true || !functions.TryAdd(Primitive.Name(function.Name), function)) throw Error(function.Position, $"Duplicate function '{function.Name}'.");
        foreach (var function in functions.Values)
        {
            var defaults = new IrExpression?[function.Parameters.Count];
            for (var i = 0; i < defaults.Length; i++)
            {
                if (i >= function.Defaults.Count || function.Defaults[i] is not { } sourceDefault) continue;
                bindingConstant = true;
                var parameterType = DeclaredType(function.Parameters[i], function.Position);
                var value = Convert(Bind(sourceDefault), parameterType, sourceDefault.Position);
                bindingConstant = false;
                if (parameterType == ValueType.String)
                {
                    if (ConstantText(value) is not { } text) throw Error(sourceDefault.Position, "Optional parameter default must be constant.");
                    defaults[i] = new IrString(text);
                }
                else if (Constant(value) is { } number)
                    defaults[i] = parameterType == ValueType.Float ? new IrFloat((float)number) : new IrInteger(unchecked((int)number));
                else throw Error(sourceDefault.Position, "Optional parameter default must be constant.");
            }
            functionDefaults.Add(Primitive.Name(function.Name), defaults);
        }
        bindingConstant = false;
        bridge = new CSharpBridge(statements, sourcePath, externals);
        foreach (var declaration in statements.OfType<TypeDeclaration>())
        foreach (var field in declaration.Fields.Where(field => field.Initializer is not null))
        {
            var fieldType = objectTypes[declaration.Name].Fields[Primitive.Name(field.Name)];
            _ = Convert(Bind(field.Initializer!), fieldType, field.Position);
        }
        // Main declarations bind sequentially; functions subsequently see every global.
        var main = BindScope(statements, [], ValueType.Integer, true);
        var boundFunctions = new List<IrFunction>();
        foreach (var function in functions.Values)
        {
            var type = DeclaredType(function.Name, function.Position);
            boundFunctions.Add(new(Primitive.Name(function.Name), type, BindScope(function.Body, function.Parameters, type, declaration: function.Position), function.Position));
        }
        return new(main, arrays.Values.ToArray(), globals.Select(g => new IrGlobal(g.Key, g.Value)).ToArray(), boundFunctions, statements.OfType<CSharp>().Where(code => code.IsModule).ToArray(), objectTypes.Values.ToArray(), data.ToArray(), usesData);
    }

    private void CollectData(IReadOnlyList<Statement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is DataLabel label && !dataLabels.TryAdd(Primitive.Name(label.Name), data.Count))
                throw Error(label.Position, $"Duplicate data label '{label.Name}'.");
            if (statement is not DataDeclaration declaration) continue;
            foreach (var expression in declaration.Values)
            {
                bindingConstant = true;
                var value = Bind(expression);
                bindingConstant = false;
                if (value.Type == ValueType.Integer && Constant(value) is double integer)
                    data.Add(new(ValueType.Integer, unchecked((int)integer), 0, null));
                else if (value.Type == ValueType.Float && Constant(value) is double floating)
                    data.Add(new(ValueType.Float, 0, (float)floating, null));
                else if (value.Type == ValueType.String && ConstantText(value) is string text)
                    data.Add(new(ValueType.String, 0, 0, text));
                else throw Error(expression.Position, "Data expression must be a constant Integer, Float or String.");
            }
        }
        bindingConstant = false;
    }

    private IrScope BindScope(IReadOnlyList<Statement> statements, IReadOnlyList<string> parameters, ValueType returnType, bool isMain = false, SourcePosition? declaration = null)
    {
        locals = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in parameters)
            if (!locals.TryAdd(Primitive.Name(name), (locals.Count, DeclaredType(name, declaration ?? statements.FirstOrDefault()?.Position ?? new(1, 1)))))
                throw Error(declaration ?? statements.FirstOrDefault()?.Position ?? new(1, 1), $"Duplicate parameter '{name}'.");
        var output = BindStatements(statements, returnType, isMain, 0);
        return new(locals.Values.Select(v => v.Type).ToArray(), parameters.Count, output, returnType);
    }

    private IReadOnlyList<IrStatement> BindStatements(IReadOnlyList<Statement> statements, ValueType returnType, bool isMain, int loopDepth)
    {
        var output = new List<IrStatement>();
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case Print print:
                    var printed = Bind(print.Value);
                    if (printed.Type.IsObject || printed.Type.IsVector) throw Error(print.Position, "Cannot print an object reference; use its fields.");
                    output.Add(new IrPrint(printed, print.Position)); break;
                case DataDeclaration or DataLabel: usesData = true; break;
                case ReadData read:
                    usesData = true;
                    foreach (var targetSyntax in read.Targets)
                    {
                        var readTarget = Bind(targetSyntax);
                        if (readTarget is not (IrVariable or IrArrayAccess or IrField or IrVectorAccess))
                            throw Error(targetSyntax.Position, "Read requires a writable variable or element.");
                        if (readTarget.Type.IsObject || readTarget.Type.IsVector)
                            throw Error(targetSyntax.Position, "Data cannot be read into an object.");
                        output.Add(new IrDataRead(readTarget, Location(read.Position), targetSyntax.Position));
                    }
                    break;
                case RestoreData restore:
                    usesData = true;
                    var index = restore.Label is null ? 0
                        : dataLabels.TryGetValue(Primitive.Name(restore.Label), out var offset) ? offset
                        : throw Error(restore.Position, $"Unknown data label '{restore.Label}'.");
                    output.Add(new IrDataRestore(index, restore.Position)); break;
                case VectorAssignment assign:
                    var vector = BindVector(assign.Target);
                    output.Add(new IrVectorAssign(vector, Convert(Bind(assign.Value), vector.Type, assign.Position), assign.Position)); break;
                case FieldAssignment assign:
                    var field = BindField(assign.Target);
                    output.Add(new IrFieldAssign(field, Convert(Bind(assign.Value), field.Type, assign.Position), assign.Position)); break;
                case Delete delete:
                    var deleted = delete.Value is null ? null : Bind(delete.Value);
                    var deletedType = deleted?.Type ?? RequireObjectType(delete.TypeName!, delete.Position);
                    if (deletedType == ValueType.Null) break; // Delete Null is an explicit no-op in the original runtime.
                    if (!deletedType.IsObject) throw Error(delete.Position, "Delete requires a typed object reference.");
                    output.Add(new IrDelete(deleted, deletedType, delete.Position)); break;
                case Insert insert:
                    var inserted = Bind(insert.Value); var anchor = Bind(insert.Anchor);
                    if (!inserted.Type.IsObject || inserted.Type == ValueType.Null || inserted.Type != anchor.Type) throw Error(insert.Position, "Insert requires objects of the same Type.");
                    output.Add(new IrInsert(inserted, anchor, insert.Before, Location(insert.Position), insert.Position)); break;
                case ForEach loop:
                    var eachTarget = Bind(loop.Target); var eachType = RequireObjectType(loop.TypeName, loop.Position);
                    if (eachTarget.Type != eachType) throw Error(loop.Position, "For Each variable must have the iteration Type.");
                    output.Add(new IrForEach(eachTarget, eachType, BindStatements(loop.Body, returnType, isMain, loopDepth + 1), loop.Position)); break;
                case ArrayAssignment assign:
                    var arrayTarget = BindArray(assign.Target);
                    output.Add(new IrArrayAssign(arrayTarget, Convert(Bind(assign.Value), arrayTarget.Type, assign.Position), assign.Position)); break;
                case Dim dim:
                    var arrayKey = Primitive.Name(dim.Name);
                    if (locals.ContainsKey(arrayKey) || globals.ContainsKey(arrayKey) || constants.ContainsKey(arrayKey)) throw Error(dim.Position, $"'{arrayKey}' is already a scalar variable.");
                    if (!arrays.TryGetValue(arrayKey, out var array))
                    {
                        if (!isMain) throw Error(dim.Position, $"Array '{arrayKey}' must be declared in the main program.");
                        array = new(arrayKey, $"global::__BlitzArrays.a{arrays.Count}", DeclaredType(dim.Name, dim.Position), dim.Bounds.Count);
                        arrays.Add(arrayKey, array);
                    }
                    CheckTag(dim.Name, array.Type, dim.Position);
                    if (array.Rank != dim.Bounds.Count) throw Error(dim.Position, $"Array '{arrayKey}' requires {array.Rank} dimensions.");
                    output.Add(new IrDim(array, dim.Bounds.Select(bound => Convert(Bind(bound), ValueType.Integer)).ToArray(), Location(dim.Position), dim.Position)); break;
                case Assignment assign:
                    var storage = Slot(assign.Name, assign.Position);
                    output.Add(new IrAssign(storage.Storage, Convert(Bind(assign.Value), storage.Type, assign.Position), assign.Position)); break;
                case Declaration declaration:
                    // The initializer sees the enclosing binding before this declaration exists.
                    var type = DeclaredType(declaration.Name, declaration.Position);
                    if (declaration.Bound is not null) type = VectorType(type, declaration.Bound);
                    var key = Primitive.Name(declaration.Name);
                    if ((declaration.IsGlobal || isMain) && constants.ContainsKey(key)) throw Error(declaration.Position, $"Duplicate identifier '{key}'.");
                    if (declaration.IsGlobal && externals?.Contains(key) == true) throw Error(declaration.Position, $"Global '{key}' conflicts with a registered external function.");
                    var initializer = declaration.Value is null ? null : Convert(Bind(declaration.Value), type, declaration.Position);
                    if (isMain && arrays.ContainsKey(key)) throw Error(declaration.Position, $"'{key}' is already an array.");
                    string target;
                    if (declaration.IsGlobal)
                    {
                        if (locals.ContainsKey(key) || !globals.TryAdd(key, type))
                            throw Error(declaration.Position, $"Duplicate variable '{key}'.");
                        if (functions.ContainsKey(key))
                            throw Error(declaration.Position, $"Global '{key}' conflicts with a function in the C# bridge.");
                        target = $"global::BB.@{key}";
                    }
                    else
                    {
                        if (isMain && globals.ContainsKey(key) || !locals.TryAdd(key, (locals.Count, type)))
                            throw Error(declaration.Position, $"Duplicate local '{key}'.");
                        target = $"v{locals[key].Slot}";
                    }
                    if (initializer is not null) output.Add(new IrAssign(target, initializer, declaration.Position));
                    break;
                case Return ret: output.Add(new IrReturn(ret.Value is null ? Default(returnType) : Convert(Bind(ret.Value), returnType, ret.Position), ret.Position)); break;
                case CSharp { IsModule: false } code: output.Add(new IrCSharp(code.Code, code.Position)); break;
                case CallStatement call: output.Add(new IrCallStatement((IrCall)Bind(call.Value, allowVoid: true), call.Position)); break;
                case End end: output.Add(new IrEnd(end.Position)); break;
                case If conditional:
                    var condition = Condition(conditional.Condition);
                    output.Add(new IrIf(condition, BindStatements(conditional.Then, returnType, isMain, loopDepth), BindStatements(conditional.Else, returnType, isMain, loopDepth), conditional.Position)); break;
                case While loop:
                    var whileCondition = Condition(loop.Condition);
                    output.Add(new IrWhile(whileCondition, BindStatements(loop.Body, returnType, isMain, loopDepth + 1), loop.Position)); break;
                case Repeat loop:
                    var repeatBody = BindStatements(loop.Body, returnType, isMain, loopDepth + 1);
                    output.Add(new IrRepeat(repeatBody, loop.Until is null ? null : Condition(loop.Until), loop.Position)); break;
                case For loop:
                    var variable = Bind(loop.Target);
                    if (variable is not (IrVariable or IrArrayAccess or IrField or IrVectorAccess)) throw Error(loop.Target.Position, "For index must be a variable, not a constant.");
                    if ((variable.Type == ValueType.String || variable.Type.IsObject)) throw Error(loop.Position, "For index must be Integer or Float.");
                    var from = Convert(Bind(loop.From), variable.Type);
                    var to = Convert(Bind(loop.To), variable.Type);
                    var step = Convert(Bind(loop.Step), variable.Type);
                    if (Constant(step) is not double stepValue) throw Error(loop.Step.Position, "Step value must be constant.");
                    output.Add(new IrFor(variable, from, to, step, stepValue > 0, BindStatements(loop.Body, returnType, isMain, loopDepth + 1), loop.Position)); break;
                case Select selection:
                    var selected = Bind(selection.Value);
                    if (selected.Type.IsObject || selected.Type.IsVector) throw Error(selection.Position, "Select requires Integer, Float or String.");
                    var cases = new List<IrCase>();
                    foreach (var item in selection.Cases)
                    {
                        var values = item.Values.Select(value => Convert(Bind(value), selected.Type)).ToArray();
                        cases.Add(new(values, BindStatements(item.Body, returnType, isMain, loopDepth), item.Position));
                    }
                    output.Add(new IrSelect(selected, cases, BindStatements(selection.Default, returnType, isMain, loopDepth), selection.Position)); break;
                case Exit exit:
                    if (loopDepth == 0) throw Error(exit.Position, "Exit must appear inside a loop.");
                    output.Add(new IrExit(exit.Position)); break;
                case TypeDeclaration or Function or CSharp or ConstantDeclaration: break;
                default: throw new InvalidOperationException("Unknown syntax statement.");
            }
        }
        return output;
    }

    private string Location(SourcePosition at) => $"{at.File ?? sourcePath}({at.Line},{at.Column})";

    private ValueType VectorType(ValueType element, Expression bound)
    {
        var size = Constant(Convert(Bind(bound), ValueType.Integer, bound.Position));
        if (size is null) throw Error(bound.Position, "Fixed vector upper bound must be constant.");
        if (size < 0) throw Error(bound.Position, "Fixed vector upper bound must not be negative.");
        if (size >= System.Array.MaxLength) throw Error(bound.Position, "Fixed vector exceeds the supported managed array size.");
        return ValueType.Vector(element, checked((int)size + 1));
    }
    private IrVectorAccess BindVector(VectorAccess access)
    {
        var owner = Bind(access.Owner);
        if (!owner.Type.IsVector) throw Error(access.Position, "Variable must be a fixed vector.");
        var index = Convert(Bind(access.Index), ValueType.Integer, access.Index.Position);
        if (Constant(index) is double constant && (constant < 0 || constant >= owner.Type.VectorLength))
            throw Error(access.Index.Position, "Fixed vector subscript out of range.");
        var name = access.Owner switch { Variable variable => variable.Name, FieldAccess field => field.Name, _ => "element" };
        return new(owner, index, Location(access.Position) + $": Vector '{name}'");
    }

    private IrArrayAccess BindArray(ArrayAccess access)
    {
        var key = Primitive.Name(access.Name);
        var indices = access.Indices.Select(index => Convert(Bind(index), ValueType.Integer)).ToArray();
        if (locals.ContainsKey(key) || globals.ContainsKey(key) || !arrays.TryGetValue(key, out var array))
            throw Error(access.Position, $"Array '{key}' was not found; a scalar may shadow it.");
        CheckTag(access.Name, array.Type, access.Position);
        if (array.Rank != indices.Length) throw Error(access.Position, $"Array '{key}' requires {array.Rank} indices.");
        return new(array.Storage, indices, array.Type, Location(access.Position));
    }

    internal IrExpression Convert(IrExpression value, ValueType type, SourcePosition? at = null)
    {
        if (value.Type.IsVector || type.IsVector) throw Error(at ?? new(1, 1), "Fixed vectors cannot be copied or passed as scalar values.");
        if (value.Type == type) return value;
        if (value.Type == ValueType.Null && type.IsObject) return new IrObjectOperation("null", type, null, "");
        if (value.Type.IsObject || type.IsObject) throw Error(at ?? new(1, 1), $"Cannot convert {value.Type} to {type}.");
        return new IrConvert(value, type);
    }
    private IrExpression Condition(Expression value)
    {
        var bound = Bind(value);
        return Convert(bound, ValueType.Integer, value.Position);
    }
    private ValueType RequireObjectType(string name, SourcePosition at)
    {
        if (!objectTypes.ContainsKey(name)) throw Error(at, $"Unknown Type '{name}'.");
        return ValueType.Object(name);
    }
    private ValueType DeclaredType(string name, SourcePosition at)
    {
        var type = Primitive.Type(name);
        return type.IsObject ? RequireObjectType(type.Name, at) : type;
    }
    private IrField BindField(FieldAccess access)
    {
        var owner = Bind(access.Owner);
        if (!owner.Type.IsObject || !objectTypes.TryGetValue(owner.Type.Name, out var type) || !type.Fields.TryGetValue(Primitive.Name(access.Name), out var field))
            throw Error(access.Position, $"Unknown field '{access.Name}' on {owner.Type}.");
        CheckTag(access.Name, field.IsVector ? field.ElementType : field, access.Position);
        return new(owner, Primitive.Name(access.Name), field, Location(access.Position));
    }
    private static IrExpression Default(ValueType type) => type.IsObject ? new IrObjectOperation("null", type, null, "") : type == ValueType.String ? new IrString("") : type == ValueType.Float ? new IrFloat(0) : new IrInteger(0);

    private IrExpression Bind(Expression expression, bool allowVoid = false)
    {
        var value = BindExpression(expression);
        if (!allowVoid && value.Type == ValueType.Void)
            throw Error(expression.Position, "Void external commands cannot be used as scalar values.");
        return value;
    }

    private IrExpression BindExpression(Expression expression)
    {
        switch (expression)
        {
            case Literal { Value: int number }: return new IrInteger(number);
            case Literal { Value: float number }: return new IrFloat(number);
            case Literal { Value: string text }: return new IrString(text);
            case FieldAccess field: return BindField(field);
            case VectorAccess vector: return BindVector(vector);
            case ObjectOperation operation:
                if (operation.Operation == "null") return new IrObjectOperation("null", ValueType.Null, null, Location(operation.Position));
                if (operation.Operation is "handle" or "fromhandle")
                {
                    if (bindingConstant) throw Error(operation.Position, "Object handles are not constant expressions.");
                    var value = Bind(operation.Value!);
                    if (operation.Operation == "handle")
                    {
                        if (!value.Type.IsObject || value.Type.IsVector) throw Error(operation.Position, "Handle requires an object reference.");
                        return value.Type == ValueType.Null ? new IrInteger(0)
                            : new IrObjectOperation("handle", ValueType.Integer, value, Location(operation.Position));
                    }
                    return new IrObjectOperation("fromhandle", RequireObjectType(operation.TypeName!, operation.Position),
                        Convert(value, ValueType.Integer, operation.Position), Location(operation.Position));
                }
                var objectValue = operation.Value is null ? null : Bind(operation.Value);
                var objectType = operation.TypeName is null ? objectValue!.Type : RequireObjectType(operation.TypeName, operation.Position);
                if (!objectType.IsObject || objectType == ValueType.Null) throw Error(operation.Position, "Object operation requires a typed reference.");
                return new IrObjectOperation(operation.Operation, objectType, objectValue, Location(operation.Position));
            case Variable variable:
                var key = Primitive.Name(variable.Name);
                if (!locals.ContainsKey(key) && constants.TryGetValue(key, out var constant))
                {
                    CheckTag(variable.Name, constant.Type, variable.Position);
                    return constant;
                }
                if (bindingConstant) throw Error(variable.Position, "Const initializer must use previously declared constants.");
                return Slot(variable.Name, variable.Position);
            case ArrayAccess access: return BindArray(access);
            case Call call:
                var arguments = call.Arguments.Select(argument => Bind(argument)).ToArray();
                if (call.IsCSharp)
                {
                    if (bindingConstant) throw Error(call.Position, "Const initializer cannot call C# methods.");
                    return bridge.Bind(call, arguments);
                }
                if (call.Name is "int" or "float" or "str")
                {
                    if (arguments.Length != 1) throw Error(call.Position, $"{call.Name} requires one argument.");
                    return Convert(arguments[0], call.Name == "int" ? ValueType.Integer : call.Name == "float" ? ValueType.Float : ValueType.String, call.Position);
                }
                if (bindingConstant) throw Error(call.Position, "Const initializer cannot call a BASIC function.");
                if (!functions.TryGetValue(Primitive.Name(call.Name), out var function)) return BindExternal(call, arguments);
                if (arguments.Length > function.Parameters.Count) throw Error(call.Position, $"Function '{call.Name}' accepts at most {function.Parameters.Count} arguments.");
                var defaultsForFunction = functionDefaults[Primitive.Name(call.Name)];
                if (arguments.Length < function.Parameters.Count && Enumerable.Range(arguments.Length, function.Parameters.Count - arguments.Length).Any(i => defaultsForFunction[i] is null))
                    throw Error(call.Position, $"Function '{call.Name}' is missing required argument {arguments.Length + 1}.");
                var resultType = DeclaredType(function.Name, function.Position);
                CheckTag(call.Name, resultType, call.Position);
                var completedArguments = new IrExpression[function.Parameters.Count];
                for (var i = 0; i < completedArguments.Length; i++)
                    completedArguments[i] = i < arguments.Length
                        ? Convert(arguments[i], DeclaredType(function.Parameters[i], call.Position), call.Position)
                        : defaultsForFunction[i]!;
                return new IrCall(Primitive.Name(call.Name), completedArguments, false, resultType);
            case Unary unary:
                var operand = Bind(unary.Operand);
                if (operand.Type.IsObject || operand.Type.IsVector) throw Error(unary.Position, "Unary arithmetic cannot be applied to object references.");
                if (operand.Type == ValueType.String) throw Error(unary.Position, "Unary operator cannot be applied to strings.");
                var unaryValue = new IrUnary(unary.Operator, operand);
                // The original folds constant unary intrinsics separately from its runtime
                // helpers; official V1.118 preserves -0 in a folded Abs but normalizes
                // runtime Float Abs to +0. Keep both observed paths distinct.
                if (unary.Operator is TokenKind.Abs or TokenKind.Sgn && Constant(unaryValue) is double foldedUnary)
                    return operand.Type == ValueType.Integer ? new IrInteger(unchecked((int)foldedUnary)) : new IrFloat((float)foldedUnary);
                return unaryValue;
            case Binary binary:
                var left = Bind(binary.Left);
                var right = Bind(binary.Right);
                if (left.Type.IsVector || right.Type.IsVector) throw Error(binary.Position, "Operators require a fixed-vector element, not the vector itself.");
                if (left.Type.IsObject || right.Type.IsObject)
                {
                    if (binary.Operator is not TokenKind.Equal and not TokenKind.NotEqual || !left.Type.IsObject || !right.Type.IsObject || left.Type != right.Type && left.Type != ValueType.Null && right.Type != ValueType.Null)
                        throw Error(binary.Position, "Object references support equality only with the same Type or Null.");
                    if (left.Type == ValueType.Null && right.Type == ValueType.Null) return new IrInteger(binary.Operator == TokenKind.Equal ? 1 : 0);
                    var common = left.Type == ValueType.Null ? right.Type : left.Type;
                    return new IrBinary(Convert(left, common), binary.Operator, Convert(right, common));
                }
                var type = left.Type == ValueType.String || right.Type == ValueType.String ? ValueType.String
                    : left.Type == ValueType.Float || right.Type == ValueType.Float ? ValueType.Float : ValueType.Integer;
                if (Operators.IsBitwise(binary.Operator)) type = ValueType.Integer;
                if (binary.Operator == TokenKind.Power && type != ValueType.String) type = ValueType.Float;
                if (type == ValueType.String && binary.Operator != TokenKind.Plus && !Operators.IsRelation(binary.Operator))
                    throw Error(binary.Position, "Operator cannot be applied to strings.");
                left = Convert(left, type); right = Convert(right, type);
                if (binary.Operator is TokenKind.Slash or TokenKind.Mod && Constant(right) == 0)
                    throw Error(binary.Position, "Division by zero.");
                if (type == ValueType.Integer && binary.Operator == TokenKind.Slash && Constant(left) == int.MinValue && Constant(right) == -1)
                    throw Error(binary.Position, "Integer division overflows the signed 32-bit range.");
                return new IrBinary(left, binary.Operator, right);
            default: throw new InvalidOperationException("Unknown syntax expression.");
        }
    }

    private static double? Constant(IrExpression expression)
    {
        if (expression is IrInteger integer) return integer.Value;
        if (expression is IrFloat floating) return floating.Value;
        if (expression is IrConvert conversion)
        {
            if (ConstantText(conversion.Value) is string text)
            {
                if (conversion.Target == ValueType.Integer)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"^[\t\n\v\f\r ]*[+-]?[0-9]+");
                    if (!match.Success) return 0;
                    var digits = match.Value.TrimStart();
                    var negative = digits[0] == '-';
                    var result = 0;
                    for (var i = digits[0] is '+' or '-' ? 1 : 0; i < digits.Length; i++) result = unchecked(result * 10 + digits[i] - '0');
                    return negative ? unchecked(-result) : result;
                }
                if (conversion.Target == ValueType.Float)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"^[\t\n\v\f\r ]*[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?");
                    return match.Success && double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? (float)number : 0;
                }
            }
            if (Constant(conversion.Value) is double value)
            {
                if (conversion.Target == ValueType.Float) return (float)value;
                if (conversion.Target == ValueType.Integer)
                {
                    var rounded = Math.Round(value, MidpointRounding.ToEven);
                    return !double.IsFinite(rounded) || rounded >= 2147483648d || rounded < -2147483648d ? int.MinValue : (int)rounded;
                }
            }
            return null;
        }
        if (expression is IrUnary unary && Constant(unary.Operand) is double operand)
            return unary.Operator switch
            {
                TokenKind.Minus => unary.Type == ValueType.Integer ? unchecked(-(int)operand) : -operand,
                TokenKind.Abs => unary.Type == ValueType.Integer ? (operand >= 0 ? operand : unchecked(-(int)operand)) : (operand >= 0 ? (float)operand : -(float)operand),
                TokenKind.Sgn => operand > 0 ? 1 : operand < 0 ? -1 : 0,
                _ => operand
            };
        if (expression is not IrBinary binary) return null;
        if (Operators.IsRelation(binary.Operator) && ConstantText(binary.Left) is string leftText && ConstantText(binary.Right) is string rightText)
        {
            var comparison = string.CompareOrdinal(leftText, rightText);
            return CompareConstant(binary.Operator, comparison, 0);
        }
        if (Constant(binary.Left) is not double left || Constant(binary.Right) is not double right) return null;
        if (Operators.IsRelation(binary.Operator)) return CompareConstant(binary.Operator, left, right);
        if (binary.Type == ValueType.Integer)
            return binary.Operator switch
            {
                TokenKind.Plus => unchecked((int)left + (int)right), TokenKind.Minus => unchecked((int)left - (int)right),
                TokenKind.Star => unchecked((int)left * (int)right),
                TokenKind.And => (int)left & (int)right, TokenKind.Or => (int)left | (int)right, TokenKind.Xor => (int)left ^ (int)right,
                TokenKind.Shl => (int)left << (int)right, TokenKind.Shr => (int)((uint)(int)left >> (int)right), TokenKind.Sar => (int)left >> (int)right,
                TokenKind.Slash when right != 0 && !(left == int.MinValue && right == -1) => (int)left / (int)right,
                TokenKind.Mod when right != 0 && !(left == int.MinValue && right == -1) => (int)left % (int)right,
                _ => null
            };
        return binary.Operator switch
        {
            TokenKind.Plus => (float)(left + right), TokenKind.Minus => (float)(left - right), TokenKind.Star => (float)(left * right),
            TokenKind.Slash when right != 0 => (float)(left / right), TokenKind.Mod when right != 0 => (float)(left % right),
            TokenKind.Power => (float)Math.Pow(left, right), _ => null
        };
    }

    private static double CompareConstant(TokenKind op, double left, double right) => op switch
    {
        TokenKind.Equal => left == right ? 1 : 0, TokenKind.NotEqual => left != right ? 1 : 0,
        TokenKind.Less => left < right ? 1 : 0, TokenKind.Greater => left > right ? 1 : 0,
        TokenKind.LessEqual => left <= right ? 1 : 0, _ => left >= right ? 1 : 0
    };

    private static string? ConstantText(IrExpression expression)
    {
        if (expression is IrString text) return text.Value;
        if (expression is IrBinary { Operator: TokenKind.Plus } binary && binary.Type == ValueType.String && ConstantText(binary.Left) is string left && ConstantText(binary.Right) is string right) return left + right;
        if (expression is IrConvert conversion && conversion.Target == ValueType.String && Constant(conversion.Value) is double value)
            return conversion.Value.Type == ValueType.Float ? FormatConstantFloat((float)value) : value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    // Keep constant casts consistent with the self-contained emitted runtime.
    private static string FormatConstantFloat(float value)
    {
        if (float.IsNaN(value)) return "NaN";
        if (float.IsPositiveInfinity(value)) return "Infinity";
        if (float.IsNegativeInfinity(value)) return "-Infinity";
        var negative = float.IsNegative(value);
        var scientific = System.MathF.Abs(value).ToString("E5", System.Globalization.CultureInfo.InvariantCulture);
        var e = scientific.IndexOf('E');
        var digits = scientific[..e].Replace(".", "");
        var exponent = int.Parse(scientific[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var dec = exponent + 1;
        string text;
        if (dec <= -3 || dec > 8)
        {
            var tail = digits[1..].TrimEnd('0');
            text = digits[..1] + "." + tail + "e" + (exponent < 0 ? "-" : "+") + System.Math.Abs(exponent).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            text = dec <= 0 ? "0." + new string('0', -dec) + digits
                : dec < 6 ? digits[..dec] + "." + digits[dec..]
                : digits + new string('0', dec - 6) + ".0";
            while (text.EndsWith("0", System.StringComparison.Ordinal) && text[^2] != '.') text = text[..^1];
        }
        return negative ? "-" + text : text;
    }
}
