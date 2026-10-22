---
title: Primitivos tipados e resolucao estatica de overloads
author: Codex
date: 2026-10-20
touches:
  - src/BlitzCS.Compiler
  - tests/BlitzCS.CLI.Tests/TypedInteropTests.cs
  - docs/typed-interop.md
---

Current contract supersedes integer-only bridge restrictions. BASIC %, #, $ map to int,float,string through variables, scopes, functions and BB fields/methods. CS:: uses a Roslyn signature probe (modules never executed), filters primitive accessible static fixed methods, chooses exact/int-to-float nondominated overloads and emits conversions explicitly. Safe small-integral returns still widen to int. BASIC casts use invariant compatibility helpers: prefix parsing, nearest-even float-to-int policy, six-significant-digit float format with .0. Do not use native C# float-to-int truncation. Original cast syntax allows unary operand without parentheses and matching optional suffix. Original-source evidence is not differential proof: x87/CRT/encoding edges and String constant-conversion folding remain documented limits. Previous84 baseline passed before change; newly-supported negative scenarios moved positive preserving same input; final132 tests and three demos passed in Release. See docs/typed-interop.md and docs/validation.md.

M4 update: primitive constant conversions now fold, including the compile-time error for 4 / Int("none"). The earlier String-folding limitation is superseded; x87/CRT/encoding still need differential verification. See docs/legacy-language.md.
