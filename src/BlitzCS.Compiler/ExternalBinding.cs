namespace BlitzCS.Compiler;

internal sealed partial class SemanticAnalysis
{
    private IrCall BindExternal(Call call, IReadOnlyList<IrExpression> arguments)
    {
        if (externals is null || !externals.TryGet(call.Name, out var function))
            throw Error(call.Position, $"Unknown BASIC function or external symbol '{call.Name}'.");
        CheckTag(call.Name, function.Result, call.Position);
        if (arguments.Count > function.Parameters.Count)
            throw Error(call.Position, $"External function '{call.Name}' accepts at most {function.Parameters.Count} arguments.");
        var converted = new IrExpression[function.Parameters.Count];
        for (var i = 0; i < converted.Length; i++)
        {
            var parameter = function.Parameters[i];
            converted[i] = i < arguments.Count ? Convert(arguments[i], parameter.Type, call.Position)
                : parameter.Default ?? throw Error(call.Position, $"External function '{call.Name}' requires argument {i + 1} ({parameter.Type}).");
        }
        return new(function.Target, converted, true, function.Result);
    }
}
