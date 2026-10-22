# Legacy Blitz3D object behavior research

This note records source findings from the original Blitz3D checkout at `/tmp/blitz-research/blitz3d`, commit `6beb288cb5962393684a59a4a44ac11524894939`. References below use paths relative to that checkout. They describe the compiler and runtime sources; they do not claim executable differential validation.

## Declarations and object types

The parser accepts `Type <name>`, zero or more `Field` rows, and `End Type`; a type declaration is only legal at main-program scope (`compiler/parser.cpp:309-311,476-492`). A field row is parsed using the same declaration parser as other variables. It can be a scalar declaration or a one-dimensional vector declaration using `[size]` (`compiler/parser.cpp:414-435`). If no type suffix/tag is present, declaration typing defaults to integer (`compiler/declnode.cpp:55-59`). `parseTypeTag` recognizes `%`, `#`, `$`, and `.Ident`; a backslash followed by an identifier builds a field access (`compiler/parser.cpp:367-399`).

All structure names are first prototyped and then their fields are semanted (`compiler/prognode.cpp:16-18`; `compiler/declnode.cpp:156-166`). This ordering allows field declarations to resolve self and forward object types. Types are global declarations: function environments resolve type names by walking to their globals (`compiler/environ.cpp:44-51`). The parser itself rejects a `Type` statement nested in a function (`compiler/parser.cpp:309-311`).

Each field receives a four-byte offset, and generated type metadata records field count and each field's type descriptor (`compiler/declnode.cpp:164-202`). Runtime object headers hold a field pointer, intrusive-list links, type descriptor, and reference count (`bbruntime/basic.h:24-40`). `New` allocates zeroed scalar slots, allocates vector fields, and adds the object to the type's used list (`bbruntime/basic.cpp:239-265`).

The requested compiler/runtime files contain no matches for directives or names `Default Field Types`, `customtag`, `fieldbackslash`, or `selfcrossrefs`. That is a statement about this source subset, not about every Blitz3D distribution or language manual. Here, omitted field type tags default to integer as described above.

## Lists and traversal

The runtime keeps circular used/free lists with sentinel objects. `First` and `Last` are implemented via next/previous traversal and skip entries whose `fields` pointer is null (`bbruntime/basic.cpp:325-347`). `Insert object Before/After` is parsed as `Insert expr Before|After expr`; the compiler requires both values to be objects of the exact same type, while null checks are emitted only in debug mode (`compiler/parser.cpp:278-286`; `compiler/stmtnode.cpp:471-486`). The runtime insertion functions unlink and relink nodes and do not validate object type or liveness (`bbruntime/basic.cpp:349-359`).

`For Each` requires a variable and named type that match exactly. Translation loops by calling a helper to fetch first and next (`compiler/stmtnode.cpp:360-398`). Ordinary variables use `_bbObjEachFirst/Next`, which store through refcount-aware `_bbObjStore`; object parameters use separate raw-slot helpers (`bbruntime/basic.cpp:361-378`). `_bbObjDeleteEach` saves the next link before deleting the current entry and checks `fields` before deleting (`bbruntime/basic.cpp:296-303`). From these operations, deleting the current object in an ordinary loop leaves its list node reachable while the loop variable retains a reference; the next step reads that node's `next` before replacing the loop variable. Deleted neighbors are skipped because traversal filters null `fields`. Insertion during traversal can affect what the next link reaches. The source has no explicit guard for reentrant or mutation-heavy iteration, so broader guarantees should not be inferred from the simple loop implementation.

## Deletion, aliases, and handles

`Delete` accepts any custom-type expression and calls `_bbObjDelete` (`compiler/stmtnode.cpp:446-454`). Deleting an object releases its string, object, and vector fields; erases any handle mapping; sets `fields` to null; then releases the object's owning reference (`bbruntime/basic.cpp:268-294`). `_bbObjStore` increments a new value before releasing the previous slot, protecting self-assignment (`bbruntime/basic.cpp:315-319`). Zero-reference objects are unlinked from the used list and linked to the free list (`bbruntime/basic.cpp:308-313`).

Deletion does not clear every alias variable. Aliases continue to point at the object shell while they retain references, but the shell's `fields` is null. Object comparison is based on the `fields` pointer, so a deleted shell compares equal to null (`bbruntime/basic.cpp:321-323`). `First`/`Last` and `Before`/`After` use null sentinels; compiler-generated null checks for field access, `Before`/`After`, and `Insert` are conditional on debug mode (`compiler/varnode.cpp:103-116`; `compiler/exprnode.cpp:553-581`; `compiler/stmtnode.cpp:480-486`). Release-mode behavior on deleted or null aliases can therefore reach unchecked runtime dereferences; do not model these as safe nullable native pointers.

Handles are assigned lazily and are non-owning map entries. Null or deleted objects convert to handle `0`; deleting an object removes its handle mapping; unknown handles and type mismatches convert back to null (`bbruntime/basic.cpp:423-438`). Object casts are syntax `Object.<Type> expr`; the expression is converted to integer handle and resolved through `_bbObjFromHandle` (`compiler/parser.cpp:636-640`; `compiler/exprnode.cpp:599-611`). `ObjectHandle expr` returns an integer handle and requires an object expression (`compiler/parser.cpp:642-645`; `compiler/exprnode.cpp:617-625`).

## Parameters, returns, and object operations

Function parameters and return values use the same type suffix/tag parser as declarations (`compiler/parser.cpp:452-471`). The compiler's normal local assignment path uses `_bbObjStore`, but a custom-type parameter is treated as a raw pointer slot for assignment (`compiler/varnode.cpp:14-18,36-45`). Function cleanup releases object locals, but not object parameters (`compiler/node.cpp:100-124`). This shows parameters are borrowed slots in this compiler implementation; avoid assuming they carry their own retained reference. Object-valued function returns are emitted as their object value (`compiler/stmtnode.cpp:408-423`).

Custom-type objects can only be compared with `=` and `<>`; other relational operators are rejected (`compiler/exprnode.cpp:451-466`). Arithmetic involving custom objects is rejected (`compiler/exprnode.cpp:374-386`). These are compiler type rules, distinct from runtime pointer representation.

## Limits of this note

This is source inspection only. No executable behavior was run. In particular, iteration under nested/reentrant loops and adversarial sequences of delete, insert, and alias reassignment are inferred only where stated from the linked-list and reference-count code; the legacy source does not document stronger guarantees.

## Evidência executável posterior à pesquisa

Após estas observações, o adaptador passou a executar os binários oficiais
V1.118 (`11.18:SoLoud`), cuja correspondência exata com este commit de fonte não
é afirmada. Capturas reais confirmaram a ordem de inserção, exclusão durante
`For Each`, igualdade de aliases excluídos com `Null`, navegação `Before`/`After`
a partir de aliases excluídos e `Delete Null` como no-op. Consulte
`original-adapter.md` e os relatórios da validação. Essas capturas não estendem
a prova às sequências adversariais ou à recuperação nativa de memória acima.
