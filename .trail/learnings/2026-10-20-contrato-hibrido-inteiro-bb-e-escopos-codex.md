---
title: Contrato hibrido inteiro BB e escopos
author: Codex
date: 2026-10-20
touches:
  - src/BlitzCS.Compiler
  - src/BlitzCS.CLI
  - docs/hybrid.md
---

Historical baseline: the primitive typed milestone supersedes integer-only restrictions. See docs/typed-interop.md for the current contract.


Hybrid source compiles to one Roslyn assembly. BB exposes lowercase BASIC globals as shared static int fields and BASIC functions as static int methods; locals are private to their BASIC scope and inline C# blocks have separate C# braces. CS:: calls preserve C# case and require int-compatible results through a typed identity helper, with no reflection. Roslyn tokenization protects ?> inside strings/comments; do not replace with regex. Declaration initializers bind before inserting the new symbol, following original declnode.cpp. Keep BlitzProgram as explicit entrypoint so module Main methods cannot change program entry. Final Release verification:49 compiler+35 CLI tests and hybrid sample prints42. Never mutate production source for test probes while another agent is building the shared workspace.
