# SCP: Containment Breach source compatibility audit

## Source and method

This audit uses the official [`Regalis11/scpcb`](https://github.com/Regalis11/scpcb) repository, default branch `master`, commit [`edb8fe0840b78f14d1aef3a0bf6174630e7be296`](https://github.com/Regalis11/scpcb/commit/edb8fe0840b78f14d1aef3a0bf6174630e7be296) (commit date 2018-09-01). The repository README identifies the game and source as CC BY-SA 3.0 and lists Blitz3D v1.108 as a requirement. The commit SHA was obtained from the GitHub API tree; only `.bb` and `.decls` source files were downloaded from that pinned commit into `/tmp/scpcb-audit-source`. No game source or assets were copied into BlitzCS or modified.

The audit is static source inspection plus actual CLI compilation probes. Counts below come from the 30 `.bb` files in that checkout. The main game's transitive `Include` closure contains 24 files and 49,137 lines. It has 21 direct imports, no cycles or repeated targets, and resolves the case-only names `dreamfilter.bb`, `menu.bb`, `save.bb` and `Drawportals.bb` on Windows. The SCP source graph does not exercise duplicate or circular imports. BlitzBasic's behavior for those cases was separately captured with the original V1.118 compiler/runtime; see [include research](include-research.md) and its five differential fixtures. The separately present `MusicPlayer.bb` imports `BlitzAL.bb`, which is absent from the checkout; `MusicPlayer.bb` is not in Main's include closure.

## Coverage against the source

| Area | Status | Evidence and gap |
|---|---|---|
| `Integer`, `Float`, `String`, scalar `Global`/`Local`, functions | Partially implemented | BlitzCS handles the typed subset. The corpus has 365 `Global` lines, 1,078 `Local` lines and 452 function-definition lines. `Const` is used on 127 lines and remains unsupported in the compiler snapshot probed below. |
| Optional function parameters | Absent | 118 function declaration lines contain default values in their parameter lists; examples include `CreateDrawPortal(...,camx#=0.0,...)` (`DrawPortals.bb:15`) and `sky_CreateSky(filename$,parent%=0)` (`Skybox.bb:2`). The current parser accepts only parameter names. |
| `If`, `While`, `For`, `Repeat`, `Select`, `Exit` | Implemented for supported expressions | The source contains thousands of these constructs. BlitzCS supports these control forms; use of an unsupported command or expression within a construct still prevents compilation. |
| `Type` / `Field`, object references, `New`/`Delete`, `For Each` | Partially implemented | The checkout has 52 type declarations, 420 fields, and 407 `For Each` statements. BlitzCS implements nominal object fields and managed list traversal. Fixed-size declarations occur 93 times across `Global`, `Local`, and `Field`; arrays embedded in fields and the source's fixed-size array declaration forms are not covered by that object implementation. |
| `Dim` arrays | Partially implemented | 74 `Dim` statements occur in the corpus. BlitzCS supports typed, inclusive-bound, multi-dimensional arrays, but the game also uses fixed-size declarations such as `Global name%[size]`, which fail lexing/parsing. |
| `Include` | Implemented; actual root project blocked before expansion | BlitzCS resolves relative paths and Windows-style case differences, suppresses repeated targets, and reports missing paths at the directive. An unmodified-module wrapper successfully included `KeyName.bb` by a relative path and printed `Esc`, `Caps Lock`, `Up`. `MusicPlayer.bb` reaches the resolver and reports the genuinely absent `BlitzAL.bb`. `Main.bb` stops during eager lexing at a fixed-size array token before parsing can expand any `Include`; therefore no claim is made that SCP's include closure has compiled. The repository's dedicated BlitzCS multi-file tests cover compiler-level include behavior. |
| `Data`, `Read`, `Restore` | Absent | The corpus contains 30 `Data`, 2 `Read`, and 1 `Restore` statement lines. They have no parser/runtime implementation in BlitzCS. |
| `Goto`, `Gosub`, labels | Absent; not used by this checkout | No such statements or labels were found among these source files. |
| C# blocks, `CS::`, `BB.` bridge | Implemented by BlitzCS; not present in audited source | These remain available for hybrid projects, but the original SCP source does not exercise them. |
| Blitz3D graphics, sound, input, file and OS APIs | Absent from the language core | The source calls the Blitz3D runtime directly. Examples include `Graphics3D`, `SetBuffer`, `CreateCamera`, `CreateMesh`, `CreateSurface`, `CreateTexture`, `RenderWorld`, `UpdateWorld`, `PositionEntity`, `LoadSound`, `PlaySound`, `ChannelPlaying`, `KeyDown`, `MouseHit`, `ReadFile`, `ReadString`, `WriteFile`, and `FileType`. BlitzCS does not supply these engine services. |
| Native library declarations | Absent from the language core | The source distribution includes declarations for FMod, BlitzMovie, DevilParticleSystem, FreeImage, zlib, CPUID, and Windows `gdi32`, `kernel32`, and `user32`, plus corresponding DLL references. These require a separate managed/runtime integration. |
| Physics | Not verified as a separate dependency | No standalone physics engine or physics import was identified in this source snapshot. This does not establish that gameplay collision behavior is absent; Blitz3D entity commands and game code still require runtime support. |

The engine boundary is deliberate: BlitzCS owns parsing, binding, code generation and the C# bridge. Blitz3DCS or another host can supply rendering, audio, input, file and native-library services without adding those dependencies to the language compiler.

## Compilation probes

The Release CLI was run against each of the 30 downloaded `.bb` files independently. `KeyName.bb` compiled; the other 29 stopped at a diagnostic. Standalone probes exercise only source reached by that file's lexer/parser; they do not prove that Main's include closure compiles. Frequent first diagnostics were fixed-size array syntax (`[`), unsupported `Const`, calls to unimplemented runtime functions, and Blitz3D command expressions. For example:

* `Main.bb` stopped during eager lexing at `Global SCP1025state#[6]` (line 291): unsupported character `[`. This occurs before parsing or expanding its includes.
* `FMod.bb` stopped at its first `Const` declaration.
* `CPU_Details.bb` stopped at `Graphics 640,480,0,2` with `Expected Equal; found '640'`.
* `Blitz_File_FileName.bb` stopped at `Right`, an unimplemented string function.
* `MusicPlayer.bb` reached include resolution and stopped because its `BlitzAL.bb` dependency is absent from the official checkout.

The first diagnostic for each standalone Release probe was:

| Source | Result / first diagnostic |
|---|---|
| `AAText.bb` | Blocked: unsupported `[` at line 3. |
| `Achievements.bb` | Blocked: `const` treated as an unsupported variable at line 3. |
| `Blitz_Basic_Bank.bb` | Blocked: expected `=` at line 24. |
| `Blitz_File_FileName.bb` | Blocked: unknown function `right` at line 28. |
| `Blitz_File_ZipApi.bb` | Blocked: unsupported `const` at line 94. |
| `CPU_Details.bb` | Blocked: command form `Graphics 640,480,0,2` at line 1. |
| `Converter.bb` | Blocked: unsupported `[` at line 142. |
| `DevilParticleSystem.bb` | Blocked: unsupported `[` at line 10. |
| `Difficulty.bb` | Blocked: unsupported `const` at line 20. |
| `DrawPortals – Kopio.bb` | Blocked: expected `)` at line 14. |
| `DrawPortals.bb` | Blocked: expected `)` at line 15. |
| `Dreamfilter.bb` | Blocked: expected `=` at line 7. |
| `FMod.bb` | Blocked: unsupported `const` at line 1. |
| `Items.bb` | Blocked: unsupported `[` at line 356. |
| `KeyName.bb` | Compiled successfully. |
| `LightMapPNG.bb` | Blocked: command form `Graphics 640,480,0,2` at line 1. |
| `LoadAllSounds.bb` | Blocked: unsupported assignment syntax at line 4. |
| `Main.bb` | Blocked during eager lexing at line 291, before `Include` expansion. |
| `MapSystem.bb` | Blocked: unsupported `[` at line 177. |
| `Menu.bb` | Blocked: unsupported `[` at line 740. |
| `MusicPlayer.bb` | Blocked: missing included file `BlitzAL.bb` at line 1. |
| `NPCs.bb` | Blocked: unsupported `[` at line 38. |
| `Particles.bb` | Blocked: unsupported `[` at line 233. |
| `RMesh_Model_Viewer.bb` | Blocked: unsupported `[` at line 568. |
| `Save.bb` | Blocked: unsupported `[` at line 66. |
| `Skybox.bb` | Blocked: expected `)` at line 2. |
| `StrictLoads.bb` | Blocked: unsupported `[` at line 30. |
| `Update.bb` | Blocked: unsupported `const` at line 10. |
| `UpdateEvents.bb` | Blocked: unsupported `[` at line 22. |
| `fullscreen_window_fix.bb` | Blocked: unsupported `const` at line 29. |

As a narrow positive integration check, a temporary wrapper outside both repositories included the unmodified `KeyName.bb` using `../scpcb-audit-source/KeyName.bb`, then printed `KeyName$(1)`, `KeyName$(58)` and `KeyName$(200)`. The Release CLI built and ran that wrapper; output was exactly `Esc`, `Caps Lock`, `Up` on separate lines. The wrapper is not part of the upstream source and does not change the 1-of-30 standalone result.

The compiler can emit a one-assembly program from supported BlitzBasic and C# modules, but the SCP project is not currently shown to compile. The static source-level include graph is a useful target; reaching a successful full build also requires substantial language coverage and a Blitz3DCS runtime for the engine APIs and native imports above.

## Reproduction

The source scan input is the official commit above. After fetching its `.bb` and `.decls` files into `/tmp/scpcb-audit-source`, reproduce all per-file probes and regenerate the report with:

```sh
python3 tools/scp_compile_probe.py \
  --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --timeout 30 --report artifacts/scp-compilation.json
```

The checked-in [probe report](../artifacts/scp-compilation.json) records a SHA-256 per source file, return code, first diagnostic, phase and whether an assembly was emitted. Its repository and commit fields are declared audit metadata; `provenance_verified_by_probe` is `false` because the script does not verify that the supplied source directory matches that commit. Verify provenance separately against the official repository before treating the declaration as source attestation. A file is reported as compiled only when the CLI exits zero and emits the requested assembly. Timeouts, launch failures and a successful exit without an assembly are classified as infrastructure results, separately from compiler diagnostics. The probe only compiles files; it never launches generated game code. The run recorded 1 successful standalone compile, 29 compiler diagnostics, and no infrastructure failures.

To reproduce the failing Main probe directly:

```sh
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build /tmp/scpcb-audit-source/Main.bb -o /tmp/scpcb-main.dll
```

The source tree was kept outside the repository so the audit does not redistribute or patch the upstream game.

## Atualização Milestone 8

A auditoria acima é o checkpoint histórico do Milestone 7. O
[relatório M8](scp-milestone8.md) mantém os resultados antes/depois no mesmo
snapshot: vetores fixos e Const implementados, 2/30 arquivos independentes
compilados (Difficulty e KeyName), Main ainda bloqueado em RuntimeError na linha16.
Nenhum fonte do jogo foi modificado e não há equivalência integral reivindicada.

## Atualização Milestone 11

O [relatório M11](scp-milestone11.md) substitui a cobertura atual dos checkpoints
históricos acima: 27/30 arquivos passam na verificação sintática isolada e a
árvore integrada de Main também passa. A compilação completa permanece em 2/30
isolados e Main para em FileSize não registrado na linha 12. Sem resolução
semântica completa ou assembly do jogo emitido. O inventário de nomes nativos
é metadado para planejar implementações reais, não um runtime simulado.
