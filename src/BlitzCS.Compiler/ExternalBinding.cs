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
            IrExpression? defaultValue = null;
            var defaultIndex = i - (signature.ParameterTypes.Count - signature.DefaultValues.Count);
            if (signature.HasDeclaredDefaults && defaultIndex >= 0)
                defaultValue = signature.DefaultValues[defaultIndex] switch
                {
                    int value => new IrInteger(value),
                    float value => new IrFloat(value),
                    string value => new IrString(value),
                    _ => throw new InvalidOperationException("Invalid external default.")
                };
            else if (!signature.HasDeclaredDefaults && externals.TryGetBinding(call.Name, out var binding))
                defaultValue = binding.Parameters[i].Default;
            if (i < arguments.Count && signature.DataPointerParameters.Contains(i))
            {
                var argument = arguments[i];
                if (argument.Type.IsObject)
                {
                    if (externals.TryGetBinding(call.Name, out var objectImplementation) && !objectImplementation.ObjectAdapterParameters.Contains(i))
                        throw Error(call.Position, $"External function '{call.Name}' argument {i + 1} requires an explicit object-data marshalling adapter; an Integer bank adapter cannot accept a BASIC object.");
                    // Keep the real object expression for declaration analysis. Compile refuses every unbound call before emission.
                    converted[i] = argument;
                }
                else if (argument.Type == ValueType.Integer) converted[i] = argument;
                else throw Error(call.Position, $"External data pointer argument {i + 1} of '{call.Name}' requires an Integer bank handle or BASIC object; no numeric/string pointer coercion is supported.");
            }
            else converted[i] = i < arguments.Count ? Convert(arguments[i], parameterType, call.Position)
                : defaultValue ?? throw Error(call.Position, $"External function '{call.Name}' requires argument {i + 1} ({parameterType}).");
        }
        var target = externals.TryGetBinding(call.Name, out var implementation) ? implementation.Target : string.Empty;
        if (implementation is null)
            UnboundExternalCalls.Add(new(call.Name, call.Position.File ?? sourcePath, call.Position.Line, call.Position.Column) { Contract = signature.Contract, BindingFailure = signature.BindingFailure });
        return new(target, converted, true, result);
    }
}
