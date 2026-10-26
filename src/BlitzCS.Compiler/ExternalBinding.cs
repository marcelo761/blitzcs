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
            if (i < arguments.Count && i < signature.PointerParameters.Count && signature.PointerParameters[i])
            {
                if (arguments[i].Type == ValueType.Null)
                {
                    converted[i] = new IrInteger(0);
                    continue;
                }
                if (arguments[i].Type.IsObject)
                {
                    converted[i] = new IrObjectOperation("marshal", ValueType.Integer, arguments[i], Location(call.Position));
                    continue;
                }
            }
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
            converted[i] = i < arguments.Count ? Convert(arguments[i], parameterType, call.Position)
                : defaultValue ?? throw Error(call.Position, $"External function '{call.Name}' requires argument {i + 1} ({parameterType}).");
        }
        var target = externals.TryGetBinding(call.Name, out var implementation) ? implementation.Target : string.Empty;
        if (implementation is null)
            UnboundExternalCalls.Add(new(call.Name, call.Position.File ?? sourcePath, call.Position.Line, call.Position.Column));
        return new(target, converted, true, result);
    }
}
