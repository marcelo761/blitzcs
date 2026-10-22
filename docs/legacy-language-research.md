# Blitz3D legacy control flow and `Dim` semantics

This note records source-level behavior in the original Blitz3D compiler/runtime at commit `6beb288` (`/tmp/blitz-research/blitz3d`). It is based on source inspection only. The original executable was unavailable, so these findings have not been differentially checked against it.

## Control flow

`If` accepts an optional `Then`. The parser decides whether it is a block form by checking whether the next token is a line terminator (`:` or newline): block form reads statements through `ElseIf`, `Else`, or `EndIf`; single-line form reads through the newline. `ElseIf` is parsed recursively as a nested `If`; `Else` uses the same line/block mode as its corresponding `If` (`compiler/parser.cpp:495-520`). Conditions are cast to integer (`compiler/stmtnode.cpp:225-230`). Integer and float expressions convert to integers; strings also convert, via `__bbStrToInt` (`compiler/exprnode.cpp:41-54`; `bbruntime/basic.cpp:145-147`). Thus truth is the converted integer being nonzero, not the original float being nonzero.

`And`, `Or`, and `Xor` share one precedence level and associate left-to-right (`compiler/parser.cpp:534-553`). They are eager, integer bitwise operations: both operands are semantically cast to integer and translated (`compiler/exprnode.cpp:334-365`). `Not` is parsed as equality with zero (`compiler/parser.cpp:534-540`).

`For` accepts integer or float index variables. Start, end, and step expressions are cast to the index type; step must be compile-time constant (`compiler/stmtnode.cpp:308-326`). Start is evaluated once; the index is advanced by step at each iteration; the end expression is evaluated at each loop test (`compiler/stmtnode.cpp:329-355`). A positive step repeats while index is `<= To`; zero or negative step repeats while index is `>= To`, since the emitted test is `index > To` for positive step and `index < To` otherwise, with false branching back into the body. In particular, step zero with initial index `>= To` can loop indefinitely unless the body exits or changes relevant state; initial index `< To` skips the body (`compiler/stmtnode.cpp:348-352`).

`Exit` jumps to the active break label. `While`, `For`, and `Repeat` install that label; `Select` does not. Therefore `Exit` inside a `Select` nested in a loop exits that loop, and `Exit` outside any loop is rejected (`compiler/stmtnode.cpp:254-260, 266-272, 324-326, 492-508, 543-546`).

`Select` evaluates its selector once into a temporary. It checks each case expression in source order, including comma-separated alternatives, and branches to the first matching case. Each case body jumps to the common end, so there is no fallthrough. The default body runs only if all comparisons fail (`compiler/stmtnode.cpp:492-538`; case-list parsing at `compiler/parser.cpp:188-214`). Objects are prohibited as selectors (`compiler/stmtnode.cpp:492-496`).

Nested statement bodies use block parsing. `Type`, `Const`, `Function`, `Global`, and `Data` are restricted to the main program; `Local` has no parser scope restriction (`compiler/parser.cpp:300-347`). A function has its own level-1 environment, populated with parameters before its statements are semantically processed (`compiler/declnode.cpp:108-120`). Functions themselves cannot be nested (`compiler/parser.cpp:319-321`).

## Arrays and `Dim`

`Dim` requires at least one dimension. The default element type is integer; `%`, `#`, and `$` select integer, float, and string respectively, and a user type tag selects object elements (`compiler/parser.cpp:437-449`; `compiler/node.cpp:164-170`; `compiler/stmtnode.cpp:88-104, 119-130`). Array dimensions are runtime expressions converted to integers. Bounds are inclusive: runtime dimensioning increments each stored bound before computing cumulative strides (`compiler/stmtnode.cpp:107-115`; `bbruntime/basic.cpp:212-220`). Allocation is zero-filled, so numeric values start at zero and string/object references at null (`bbruntime/basic.cpp:192-220`).

Each execution of a `Dim` first undims the old storage, releasing string/object elements and freeing the buffer, then evaluates the new bounds and allocates a fresh zeroed buffer. Existing contents are lost; this source has no `ReDim` or `Preserve` syntax or helper (`compiler/stmtnode.cpp:107-115`; `bbruntime/basic.cpp:192-220`). Repeated `Dim` must use the existing rank and, when explicitly tagged, its existing element type (`compiler/stmtnode.cpp:88-105`). A function may `Dim` an array already declared in main, which resets the shared global storage; it may not create a new array (`compiler/stmtnode.cpp:88-104`). Arrays live in global declarations; ordinary local variables can shadow a global name (`compiler/environ.cpp:26-35`).

Indexed access converts every index to integer and requires the declared rank (`compiler/varnode.cpp:71-80`). Debug builds check the progressively flattened offset against the cumulative scale at each dimension and report `Array index out of bounds`; they do not check for negative indices (`compiler/varnode.cpp:83-97`; `bbruntime/basic.cpp:223-225`). Since checks are on the flattened offset, they do not independently validate each coordinate. With debug checks disabled, no bounds check is emitted.

Array names are recognized by the parser only after its source-order `Dim` declaration has been parsed. Before that point, `name(...)` is parsed as a function call (`compiler/parser.cpp:437-449, 737-751`). Therefore a `Dim` must precede array references in source, including references in an earlier function body; there is no forward `Dim` visibility.

## Evidence limits

The original executable was missing. Project code navigation and indexing use the installed Trail-Code CLI by its absolute local path; upstream source inspection uses the separate reference checkout. The line references above point to the inspected source tree at `/tmp/blitz-research/blitz3d`, commit `6beb288`; no compiler or runtime files were changed.
