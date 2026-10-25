# External signatures

External declarations let the compiler check BASIC calls before executable implementations are available. A declaration contains a name, return type and parameter types. Parameters support `Integer`, `Float` and `String`. Results also support **`Void`**, for commands that do not produce a value.

```json
{
  "functions": [
    {
      "name": "Graphics3D",
      "returnType": "Void",
      "parameters": ["Integer", "Integer", "Integer", "Integer"],
      "defaults": [0, 0]
    }
  ]
}
```

This declares the historical call shape `Graphics3D width,height[,depth,mode]`. It does **not** implement a renderer, open a window or create an executable binding.

To bind a declaration, include `assembly`, `type` and `method`. The public static method must have the declared primitive parameter types and primitive or `void` result. If `returnType` is present with a binding, it must match that method. Existing catalogs may omit `returnType`; method metadata supplies it. A signature without a method requires `returnType`.

Run semantic validation with:

```sh
blitzcs check-semantics main.bb --externals externals.json
```

This checks syntax and BASIC semantics against declared types and lists unbound calls with their source positions. Success does not mean these calls can be linked or run. `blitzcs build` requires an implementation for every used external call and diagnoses missing implementations at their BASIC source positions. Unused declarations do not block a build. Neither signature inspection nor compilation invokes the external method or its type initializer.

## Optional parameters

`defaults` contains constants for the **trailing** parameters, in parameter order. It may contain at most as many values as there are parameters. Values must match their declared types: Int32, finite Single or String. Objects, null, arrays and nonfinite numbers are rejected. Integer defaults are not silently coerced from floating JSON values.

- When `defaults` is absent, a bound method retains its existing CLR optional defaults; an unbound declaration has no implicit defaults.
- An explicit list overrides CLR defaults for BASIC calls. `[]` explicitly requires every argument.
- Generated calls always supply all arguments. This does not change the bound method's ABI or the defaults seen by direct C# callers.

The host API offers the same contract:

```csharp
var signature = new ExternalFunctionSignature(
    "Graphics3D", ExternalValueType.Void,
    new[] { ExternalValueType.Integer, ExternalValueType.Integer,
            ExternalValueType.Integer, ExternalValueType.Integer },
    new object[] { 0, 0 });
var registry = ExternalFunctionRegistry.FromSignatures(new[] { signature });
```

The three-argument constructor remains available. Parameter/default collections are immutable snapshots. Use actual `float` objects for Single defaults through the .NET API.

## Commands without values

A `Void` external may appear as a call statement. Using it in `Print`, an assignment, arithmetic, a condition, an argument or a BASIC return expression produces a source diagnostic before Roslyn emission. `Void` is not a valid parameter type. The primitive `CS::` bridge keeps its existing restrictions; external registration is a separate interface.

See [M17 compatibility progress](legacy-compatibility-m17.md) for genuine original probes, declaration-only graphics and the executable diagnostic command.
