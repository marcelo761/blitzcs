---
title: Controle legado arrays e diferenciais pendentes
author: Codex
date: 2026-10-21
touches:
  - src/BlitzCS.Compiler
  - tools/differential.py
  - docs/legacy-language.md
---

## Decided

- Preserve legacy control semantics: For evaluates the start once, re-evaluates To, and requires constant Step. A positive step uses <=; zero or negative uses >=. Indexed counters re-evaluate indices on each load/store.
- And/Or/Xor are eager bitwise operators at the same precedence. Not is logical negation of the group. Select evaluates its selector once; Exit targets the surrounding loop, not Select.
- Dim bounds are inclusive, with first-axis-fast layout. Each execution clears storage before evaluating new bounds. Repeated Dim keeps rank/type and discards data. Functions may access and re-dimension existing main arrays, but cannot create new arrays. Source-order array recognition and scalar shadowing follow original rules.
- Array storage is internal, outside BB. Scalar wrappers and typed callbacks remain available. Negative, per-axis and allocation guards intentionally exceed original debug cumulative checks and unchecked release behavior.
- A harness timeout can never pass or match. Mock protocol data is never a captured original result.

## Verified

Release: 275 .NET tests (141 compiler, 134 CLI), nine Python harness tests, five examples and 15 local fixtures passed. All 132 prior cases remain unchanged and passing. Original comparisons: 15 pending, zero performed.

Primitive constant conversions now fold; 4 / Int("none") is a compile-time division-by-zero diagnostic. This supersedes the prior typed-milestone folding limitation.

## Unverified

Original array-assignment operand side-effect order, x87/CRT edges and encoding remain without executable differential evidence. No original executable or graphical-output capture adapter was available. See docs/legacy-language.md, docs/differential.md and docs/validation.md.
