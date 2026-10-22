namespace BlitzCS.Compiler;

internal sealed partial class SemanticAnalysis
{
    private IrCall BindExternal(Call call, IReadOnlyList<IrExpression> arguments)
    {
        if (externals is null || !externals.TryGetSignature(call.Name, out var signature))
            throw Error(call.Position, $"Unknown BASIC function or external symbol '{call.Name}'.");
        var result = ExternalFunction.ToInternal(signature.ReturnType);
        CheckTag(call.Name, result, call.Position);
        if (arguments.Count > signature.ParameterTypes.Count)
            throw Error(call.Position, $"External function '{call.Name}' accepts at most {signature.ParameterTypes.Count} arguments.");
        var converted = new IrExpression[signature.ParameterTypes.Count];
        for (var i = 0; i < converted.Length; i++)
        {
            var parameterType = ExternalFunction.ToInternal(signature.ParameterTypes[i]);
            var bindingHasDefault = externals.TryGetBinding(call.Name, out var binding) && i < binding.Parameters.Count
                ? binding.Parameters[i].Default : null;
            converted[i] = i < arguments.Count ? Convert(arguments[i], parameterType, call.Position)
                : bindingHasDefault ?? throw Error(call.Position, $"External function '{call.Name}' requires argument {i + 1} ({parameterType}).");
        }
        var target = externals.TryGetBinding(call.Name, out var implementation) ? implementation.Target : string.Empty;
        if (implementation is null)
            UnboundExternalCalls.Add(new(call.Name, call.Position.File ?? sourcePath, call.Position.Line, call.Position.Column));
        return new(target, converted, true, result);
    }
}
