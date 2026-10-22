---
title: Primeira fatia e host de console
author: Codex
date: 2026-10-20
touches:
  - src/BlitzCS.Compiler
  - src/BlitzCS.CLI
  - docs/architecture.md
---

Historical baseline: the primitive typed milestone supersedes integer-only restrictions. See docs/typed-interop.md for the current contract.


BlitzCS starts with a bounded compiler subset: integer assignments/arithmetic, Print and End. Original Blitz3D Print draws graphics; Console.WriteLine is an explicit validation host, not graphical or full semantic compatibility. Keep unsupported syntax diagnostic, preserve .bb locations, and require differential tests before compatibility claims. Compiler stays pure; CLI owns Roslyn, filesystem artifacts and .NET runtime config. SDK is installed outside PATH at /var/home/marcelo/.local/share/causoryn-dotnet.
