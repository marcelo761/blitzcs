# M9 language compatibility: defaults, hex literals, and fields

## Evidence consulted

The historical reference source is `/tmp/blitz-research/blitz3d`, pinned in
the local research record at commit `6beb288cb5962393684a59a4a44ac11524894939`.
The executable checks use the official Blitz3D V1.118 adapter through Proton;
the independently captured original outputs and fixture hashes are recorded
in [m9-original-research.md](m9-original-research.md) and
`tests/Compatibility/milestone9.json`. The source checkout explains the
parser/compiler implementation; the V1.118 executable supplies the behavioral
comparison.

## Semantics implemented

- **Optional function parameters:** an initializer on a formal parameter must
  be a constant expression and is converted to the parameter's `%`, `#`, or
  `$` type. A call may omit only trailing parameters, which are expanded into
  the ordinary fixed-arity call at compile time. Every omitted position needs
  its own default. Empty argument slots such as `F(, 2)` are syntax errors in
  Blitz3D and BlitzCS. This retains the existing function ABI and C# bridge.
- **Argument conversion:** supplied arguments use BlitzCS's existing BASIC
  primitive conversions to the formal parameter type. The original-runtime
  fixture covers Float-to-Integer rounding, numeric String prefixes, and
  Integer/Float-to-String conversions in function calls. Command-style calls
  are handled by the separate external-call parser work.
- **Hexadecimal literals:** `$` followed by one or more hexadecimal digits is
  an Integer literal. Accumulation wraps to the low 32 bits and the resulting
  `System.Int32` is signed (`$80000000` is `-2147483648`, `$FFFFFFFF` is `-1`).
  The same lexer/parser path makes the form available in `Const` expressions.
- **Field initializers:** Blitz3D V1.118 accepts scalar and object initializer
  syntax but new instances still contain zero, empty String, or Null. BlitzCS
  accepts the declaration and checks/converts the expression, while emitted
  object allocation keeps those observed original defaults. This is deliberate
  compatibility behavior; it does not treat the declaration expression as a
  per-instance initializer.

## Source references

- `compiler/toker.cpp:153-163` recognizes binary and hexadecimal constants.
- `compiler/parser.cpp:452-474` parses formal parameters and `Field` declarations.
- `compiler/declnode.cpp:52-79` requires parameter defaults to be constant,
  casts them to the formal type, and stores a typed default.
- `compiler/parser.cpp:523-532` and `compiler/exprnode.cpp:112-139` establish
  that only omitted trailing arguments are completed from defaults; an empty
  slot after a comma is rejected.
- `compiler/parser.cpp:699-712` accumulates hex digits in a 32-bit `int`.

## Limits and validation

The verified native fixtures exercise omitted trailing defaults, primitive
conversions, ordinary integer hex overflow, and default field values. Dynamic
field initializer expressions, vector-field initializers, and the numerical
edge cases of extremely long hexadecimal tokens remain unverified. No claim is
made that all optional-argument conversion edge cases match the legacy runtime.

The BlitzCS implementation is in `Syntax.cs`, `Semantics.cs`, and
`CSharpEmitter.cs`; focused managed execution tests are in
`LanguageExpansionTests.cs`. The external symbol registry and its engine
implementations remain separate from this language behavior.
