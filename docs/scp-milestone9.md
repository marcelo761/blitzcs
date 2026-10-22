# SCP audit — milestone 9

This audit separates front-end failures from unresolved runtime names and missing external inputs. It compiles each of the 30 pinned `.bb` files on its own, against the stable Release CLI, and never runs SCP code. The source snapshot is `/tmp/scpcb-audit-source`; the repository and commit labels are carried over from the prior report, but this probe does not verify provenance. No SCP files are edited, and no engine commands are mocked.

## Baseline before M9 compiler changes

`artifacts/scp-m9-before.json` preserves the pre-M9 Release capture from `artifacts/scp-m8-after.json` and adds the new categories. The CLI path in that capture is `src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll`; the configured SDK path was `/var/home/marcelo/.local/share/causoryn-dotnet/dotnet`. The report names the `tools/scp_native_catalog.json` projection used to distinguish native names from unknown BASIC functions. That projection stores source file hashes for the original `bbruntime` files whose `rtSym` declarations it contains. Preserving the capture matters because the Release output was rebuilt during this audit.

| Classification | Files | Meaning |
|---|---:|---|
| Compiled | 2 | `Difficulty.bb`, `KeyName.bb` |
| Lexical | 0 | No first diagnostic matched a lexer failure |
| Syntactic | 25 | Unsupported or malformed syntax stopped compilation first |
| Semantic/type | 0 | No first diagnostic fell into this group |
| Native unregistered | 1 | `Right` in `Blitz_File_FileName.bb` |
| External missing | 2 | `FSOUND_Init` in `FMod.bb` is declared by `fmod.decls`; `BlitzAL.bb` included by `MusicPlayer.bb` is absent |
| Infrastructure | 0 | No timeout or CLI launch failure |

A file contributes only its first diagnostic. These counts do not describe later errors in the same file, and 2/30 is an individual-file compile rate, not a whole-project result. The `external_missing` bucket is evidence of a missing external declaration target or include in this snapshot; it does not prove that an external DLL could not be available in another setup.

## Static native command inventory

The lists below are a static intersection of identifiers in code (BASIC comments and quoted string literals removed) in the 30 `.bb` files and `rtSym` declarations in the original Blitz3D `bbruntime` C++ source. They are candidate names, not a list of parsed or resolved calls and not a list of unresolved compiler diagnostics: an identifier may be a variable or may occur in an unreachable section. Counts are unique candidate names per subsystem, not call counts. Some entries, including `End` and `Print`, are already supported by the BlitzCS core. Graphics and entity/scene entries require separate Blitz3DCS runtime integration.

| Runtime subsystem | Required names | Count |
|---|---|---:|
| Core / process / streams | AppTitle, DebugLog, Delay, End, Eof, ExecFile, MilliSecs, ReadAvail, ReadByte, ReadFloat, ReadInt, ReadLine, ReadShort, ReadString, RuntimeError, SystemProperty, WriteByte, WriteFloat, WriteInt, WriteLine, WriteString | 21 |
| Strings | Asc, Chr, CurrentDate, CurrentTime, Hex, Instr, Left, Len, Lower, LSet, Mid, Replace, Right, String, Trim, Upper | 16 |
| Math | ATan2, Ceil, Cos, Floor, Rand, Rnd, RndSeed, SeedRnd, Sin, Sqr | 10 |
| Filesystem | CloseDir, CloseFile, CopyFile, CreateDir, CurrentDir, DeleteDir, DeleteFile, FilePos, FileSize, FileType, NextFile, OpenFile, ReadDir, ReadFile, SeekFile, WriteFile | 16 |
| Input | FlushKeys, FlushMouse, GetKey, KeyDown, KeyHit, MouseDown, MouseHit, MouseX, MouseXSpeed, MouseY, MouseYSpeed, MouseZSpeed, MoveMouse, WaitKey | 14 |
| Audio | ChannelPan, ChannelPitch, ChannelPlaying, ChannelVolume, FreeSound, LoadSound, PauseChannel, PlaySound, ResumeChannel, StopChannel | 10 |
| 2D graphics / image / text | AvailVidMem, BackBuffer, Cls, ClsColor, Color, ColorBlue, ColorGreen, ColorRed, CopyRect, CountGfxDrivers, CountGfxModes3D, CreateImage, DrawImage, DrawImageRect, Flip, FontHeight, FontWidth, FreeFont, FreeImage, GetColor, GfxDriverName, GfxModeHeight, GfxModeWidth, Graphics, Graphics3D, GraphicsBuffer, GraphicsHeight, GraphicsWidth, HandleImage, HidePointer, ImageBuffer, ImageHeight, ImageRectOverlap, ImageWidth, Input, Line, LoadAnimImage, LoadFont, LoadImage, LockBuffer, MaskImage, MidHandle, Oval, Print, ReadPixelFast, Rect, ResizeImage, RotateImage, ScaleImage, SetBuffer, SetFont, ShowPointer, StringHeight, StringWidth, Text, TotalVidMem, UnlockBuffer, Windowed3D, WritePixelFast | 59 |
| 3D entity / scene / camera / mesh / texture / lighting | ActiveTextures, AddMesh, AddTriangle, AddVertex, AlignToVector, AmbientLight, AnimTime, AntiAlias, BrushTexture, CameraClsColor, CameraClsMode, CameraFogColor, CameraFogMode, CameraFogRange, CameraPick, CameraProject, CameraProjMode, CameraRange, CameraViewport, CameraZoom, ClearSurface, ClearWorld, Collisions, CollisionY, CopyEntity, CopyMesh, CountChildren, CountCollisions, CountSurfaces, CountTriangles, CountVertices, CreateBrush, CreateCamera, CreateCube, CreateLight, CreateMesh, CreatePivot, CreatePlane, CreateSphere, CreateSprite, CreateSurface, CreateTexture, DeltaPitch, DeltaYaw, EntityAlpha, EntityAutoFade, EntityBlend, EntityBox, EntityClass, EntityCollided, EntityColor, EntityDistance, EntityFX, EntityInView, EntityName, EntityOrder, EntityParent, EntityPick, EntityPickMode, EntityPitch, EntityRadius, EntityRoll, EntityShininess, EntityTexture, EntityType, EntityVisible, EntityX, EntityY, EntityYaw, EntityZ, FindChild, FlipMesh, FreeBrush, FreeEntity, FreeTexture, GetBrushTexture, GetChild, GetEntityType, GetMatElement, GetParent, GetSurface, GetSurfaceBrush, HideEntity, LightColor, LightConeAngles, LightRange, LinePick, LoadAnimMesh, LoadAnimTexture, LoadBrush, LoadMesh, LoadSprite, LoadTexture, MeshCullBox, MeshDepth, MeshHeight, MeshWidth, MoveEntity, NameEntity, PaintSurface, PickedEntity, PickedNX, PickedNY, PickedNZ, PickedX, PickedY, PickedZ, PointEntity, PositionEntity, PositionMesh, PositionTexture, ProjectedX, ProjectedY, RenderWorld, ResetEntity, RotateEntity, RotateMesh, ScaleEntity, ScaleMesh, ScaleSprite, ScaleTexture, SetAnimTime, ShowEntity, SpriteViewMode, TextureBlend, TextureBuffer, TextureCoords, TextureHeight, TextureName, TextureWidth, TFormedX, TFormedY, TFormedZ, TFormPoint, TFormVector, TranslateEntity, TriangleVertex, TrisRendered, TurnEntity, UpdateNormals, UpdateWorld, VectorYaw, VertexAlpha, VertexBlue, VertexColor, VertexCoords, VertexGreen, VertexNX, VertexNY, VertexNZ, VertexRed, VertexTexCoords, VertexU, VertexV, VertexX, VertexY, VertexZ, WireFrame | 158 |

The remaining compiler gaps span standard string support (`Right` is surfaced as a blocker), file-level syntax, and engine commands that belong in Blitz3DCS. External declarations and libraries remain a separate integration concern.

## Classification rules

The probe preserves the original `phase` field and adds a `category` to each result. Lexical diagnostics are recognized before parser diagnostics. Parser wording such as `Expected ...`, `Unexpected token`, unsupported syntax, and `Unsupported keyword ... cannot be used as a variable` is syntactic. For `Unknown BASIC function or external symbol`, a name in a sidecar `.decls` file is `external_missing`; a name in the original `rtSym` catalog is `native_unregistered`; an unlisted name is `semantic_type` (an unresolved BASIC function or other semantic binding). Missing includes are `external_missing`. The classifier describes the first observed compiler message, not a complete root-cause analysis.

For a real external module, pass the CLI catalog through the probe with `--externals path/to/catalog.json`. Each per-file build then receives `--externals <absolute-path>`; omit this option for the before capture so it records the baseline with the external registry disabled.

## M9 Release probe

The M9 Release probe ran against the same 30 files, first without the external catalog and then with `samples/Native/runtime.json`. Both reports preserve the same source hashes. Each has 2 compiled, 15 syntactic, 1 semantic/type, 10 known-native-but-unregistered, 2 external-missing, 0 lexical, and 0 infrastructure results. The principal report is `artifacts/scp-m9-after.json`; `artifacts/scp-m9-after-no-externals.json` isolates native registry results without external bindings.

The first blockers now visible include unsupported statements in `Converter.bb`, `LoadAllSounds.bb`, `Menu.bb`, `NPCs.bb`, and `Save.bb`; unsupported keyword/identifier collisions (`Object`, `Handle`, `Abs`, `Wend`, and `Restore`); known Blitz3D commands still unregistered in this compiler (`BankSize`, `Right`, `Graphics`, `CreateTexture`, `CreateCamera`, `SystemProperty`, `Graphics3D`, and `Input`); the unresolved BASIC function `GetIniInt`; and the existing `FSOUND_Init` declaration plus missing `BlitzAL.bb`. The ten `Unsupported keyword ... cannot be used as a variable` diagnostics are syntactic/parser rejections, not failed function binding. In particular, `Abs` is reserved by the parser, but this build does not yet expose its numeric runtime function. The audit uses no replacement or mock for it. This is still a first-blocker report per file, not an exhaustive feature checklist.

The two 30-file reports have identical first diagnostics. The external catalog therefore does not change the first blocker in these independently compiled units: `Main.bb` reaches an error inside included `StrictLoads.bb` before its `RuntimeError` call. A separate temporary source containing `RuntimeError "audit probe"` compiled successfully with the real `samples/Native/runtime.json` catalog. That verifies external catalog resolution at compile time; the probe does not execute runtime error behavior.

The final Release re-run after parser grouping, colon handling, and copy guards preserves all 30 source hashes from the baseline. Both final captures—without external bindings and with `samples/Native/runtime.json`—report 2 compiled, 15 syntactic, 1 semantic/type, 10 native-unregistered, and 2 external-missing files; their first diagnostics and categories match exactly. The ten reserved-keyword errors are now counted as syntax, leaving `GetIniInt` as the sole semantic blocker. A separate compile-only source confirms `RuntimeError` resolves through the real external catalog, while the SCP entry file still stops in `StrictLoads.bb` before reaching that call. This checkpoint records individual-file compilation only; it does not demonstrate that SCP builds as a project or runs in a Blitz3D engine.

## Matriz por arquivo: antes e depois

Dos 30 arquivos, 27 mudaram de primeiro diagnóstico; os 30 hashes permanecem
idênticos. `Difficulty.bb` e `KeyName.bb` continuam os únicos compilados.
A matriz registra a captura histórica M8 classificada e a execução Release M9;
a ordem do primeiro erro não prova que todos os trechos posteriores foram aceitos.

| Arquivo | Antes | Depois | Categoria M9 |
|---|---|---|---|
| `AAText.bb` | `AAText.bb(31,18): Expected Equal; found 'cam'.` | `AAText.bb(105,22): Unsupported keyword 'object' cannot be used as a variable.` | syntactic |
| `Achievements.bb` | `Achievements.bb(37,49): Expected Close; found '='.` | `Achievements.bb(19,26): Unknown BASIC function or external symbol 'getiniint'.` | semantic_type |
| `Blitz_Basic_Bank.bb` | `Blitz_Basic_Bank.bb(24,12): Expected Equal; found 'bankhandle'.` | `Blitz_Basic_Bank.bb(21,21): Unknown BASIC function or external symbol 'banksize'.` | native_unregistered |
| `Blitz_File_FileName.bb` | `Blitz_File_FileName.bb(28,5): Unknown function 'right'.` | `Blitz_File_FileName.bb(28,5): Unknown BASIC function or external symbol 'right'.` | native_unregistered |
| `Blitz_File_ZipApi.bb` | `Blitz_File_ZipApi.bb(234,62): Expected Close; found '='.` | `Blitz_File_ZipApi.bb(247,29): Unknown BASIC function or external symbol 'banksize'.` | native_unregistered |
| `Converter.bb` | `Converter.bb(1,12): Expected Equal; found '640'.` | `Converter.bb(121,5): Unsupported syntax 'function'; expected a statement separator.` | syntactic |
| `CPU_Details.bb` | `CPU_Details.bb(1,10): Expected Equal; found '640'.` | `CPU_Details.bb(1,1): Unknown BASIC function or external symbol 'graphics'.` | native_unregistered |
| `DevilParticleSystem.bb` | `DevilParticleSystem.bb(51,9): Expected Equal; found 'millisecs'.` | `DevilParticleSystem.bb(56,15): Unsupported keyword 'handle' cannot be used as a variable.` | syntactic |
| `Difficulty.bb` | `Compilado` | `Compilado` | compiled |
| `DrawPortals – Kopio.bb` | `DrawPortals – Kopio.bb(14,76): Expected Close; found '='.` | `DrawPortals – Kopio.bb(25,12): Unknown BASIC function or external symbol 'createtexture'.` | native_unregistered |
| `DrawPortals.bb` | `DrawPortals.bb(15,76): Expected Close; found '='.` | `DrawPortals.bb(21,12): Unknown BASIC function or external symbol 'createtexture'.` | native_unregistered |
| `Dreamfilter.bb` | `Dreamfilter.bb(7,17): Expected Equal; found 'cam'.` | `Dreamfilter.bb(6,15): Unknown BASIC function or external symbol 'createcamera'.` | native_unregistered |
| `FMod.bb` | `FMod.bb(15,1): Unknown function 'fsound_init'.` | `FMod.bb(15,1): Unknown BASIC function or external symbol 'fsound_init'.` | external_missing |
| `fullscreen_window_fix.bb` | `fullscreen_window_fix.bb(30,20): Expected an expression.` | `fullscreen_window_fix.bb(45,23): Unknown BASIC function or external symbol 'systemproperty'.` | native_unregistered |
| `Items.bb` | `Items.bb(31,115): Expected Close; found '='.` | `Items.bb(409,24): Unsupported keyword 'abs' cannot be used as a variable.` | syntactic |
| `KeyName.bb` | `Compilado` | `Compilado` | compiled |
| `LightMapPNG.bb` | `LightMapPNG.bb(1,12): Expected Equal; found '640'.` | `LightMapPNG.bb(1,1): Unknown BASIC function or external symbol 'graphics3d'.` | native_unregistered |
| `LoadAllSounds.bb` | `LoadAllSounds.bb(4,19): Unsupported syntax '='; expected a statement separator.` | `LoadAllSounds.bb(4,15): Expected Close; found ','.` | syntactic |
| `Main.bb` | `Main.bb(16,15): Expected Equal; found 'The following DLLs were not found in the game directory:'.` | `StrictLoads.bb(59,20): Unsupported keyword 'object' cannot be used as a variable.` | syntactic |
| `MapSystem.bb` | `MapSystem.bb(35,19): Expected Equal; found 'mat'.` | `MapSystem.bb(1577,5): Unsupported keyword 'abs' cannot be used as a variable.` | syntactic |
| `Menu.bb` | `Menu.bb(6,11): Expected Equal; found 'menublack'.` | `Menu.bb(385,20): Unsupported keyword 'abs' cannot be used as a variable.` | syntactic |
| `MusicPlayer.bb` | `MusicPlayer.bb(1,1): Unable to open include file 'BlitzAL.bb': File or directory not found.` | `MusicPlayer.bb(1,1): Unable to open include file 'BlitzAL.bb': File or directory not found.` | external_missing |
| `NPCs.bb` | `NPCs.bb(42,21): Expected Separator; found '='.` | `NPCs.bb(771,18): Unsupported keyword 'abs' cannot be used as a variable.` | syntactic |
| `Particles.bb` | `Particles.bb(17,71): Expected Close; found '='.` | `Particles.bb(125,10): Unsupported keyword 'abs' cannot be used as a variable.` | syntactic |
| `RMesh_Model_Viewer.bb` | `RMesh_Model_Viewer.bb(19,12): Expected Equal; found 'xe_xf'.` | `RMesh_Model_Viewer.bb(714,16): Unknown BASIC function or external symbol 'input'.` | native_unregistered |
| `Save.bb` | `Save.bb(20,14): Expected Equal; found 'f'.` | `Save.bb(594,14): Expected Close; found ','.` | syntactic |
| `Skybox.bb` | `Skybox.bb(2,41): Expected Close; found '='.` | `Skybox.bb(5,2): Unsupported keyword 'restore' cannot be used as a variable.` | syntactic |
| `StrictLoads.bb` | `StrictLoads.bb(17,42): Expected Equal; found 'Image '.` | `StrictLoads.bb(59,20): Unsupported keyword 'object' cannot be used as a variable.` | syntactic |
| `Update.bb` | `Update.bb(21,39): Expected Close; found '='.` | `Update.bb(838,5): Unsupported keyword 'wend' cannot be used as a variable.` | syntactic |
| `UpdateEvents.bb` | `UpdateEvents.bb(45,18): Expected Equal; found 'fog'.` | `UpdateEvents.bb(228,65): Unsupported keyword 'abs' cannot be used as a variable.` | syntactic |

Próxima fatia de linguagem indicada pelos erros reais: `Object`/`Handle`,
funções numéricas reservadas como `Abs`, `Data/Read/Restore` e as formas restantes
de statements/declaradores. `GetIniInt` é uma dependência BASIC de outro módulo
no contexto isolado; não é um erro comprovado do fonte original. Símbolos de
engine continuam fora do compilador, com implementações reais a cargo do runtime.
`CreateEntity` não aparece no catálogo original consultado: comandos como
`CreatePivot`, `CopyEntity` e `CreateCamera` são nomes reais encontrados.
