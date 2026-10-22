# External signatures

External declarations let the compiler check a BASIC call before an executable implementation is available. A declaration contains a BASIC name, a return type, and parameter types. The supported types are `Integer`, `Float`, and `String`; external functions cannot return `Void`.

```json
{
  "functions": [
    {
      "name": "NativeScale",
      "returnType": "Float",
      "parameters": ["Float"]
    }
  ]
}
```

To bind a declaration, include `assembly`, `type`, and `method`. The public static method must have the declared primitive parameter and return types. If `returnType` is present with a binding, it must match the method. Existing catalogs may omit `returnType`; the method supplies it. A signature without a method requires `returnType`.

Run semantic validation with:

```sh
blitzcs check-semantics main.bb --externals externals.json
```

This checks syntax and BASIC semantics against the declared types, and lists calls whose signatures have no implementation with their source positions. Success means semantic validation passed; it does not mean the external calls can be linked or run. `blitzcs build` requires an implementation for each external function used by the program and reports an unresolved call at its source position. Unused declarations do not block a build.

Signature-only declarations have no optional parameters: every declared argument is required. Bound methods may retain optional primitive defaults from .NET method metadata. Neither signature inspection nor compilation invokes the external method or runs its type initializer.
