# Licensing scope

## Original project material

The Apache License, Version 2.0 in [LICENSE](LICENSE) applies to original project-authored implementation, tests, tools, examples and documentation in this repository, to the extent that the project has the right to license those contributions. This includes the independently implemented `BlitzCS.*` and `Blitz3DCS.*` components. The project creator and lead is Marcelo Buratti de Freitas Filho; contributor attribution in Git remains intact.

This grant does not replace third-party licenses or establish ownership of third-party work. It does not retroactively change licenses on upstream projects, quoted reference material, dependencies or separately licensed files. Earlier commits retain their historical contents; the current licensing declaration does not rewrite them.

## Exclusions and distinct terms

| Material | Applicable terms |
| --- | --- |
| `licenses/*` and upstream notices | Their original license texts and copyright notices, retained verbatim |
| `tests/Blitz3DCS.Audio.Tests/Fixtures/generated-tone.ogg` | Existing CC0-1.0 dedication in the adjacent README; [full text](licenses/CC0-1.0.txt) |
| `docs/evidence/m15-fmod/tone.wav` | Project-generated synthetic sine-wave fixture; original project material under Apache-2.0 |
| Quoted Blitz3D reference declarations or source excerpts, where present | Original Blitz Research Ltd attribution and zlib terms; see [notices](THIRD_PARTY_NOTICES.md) |
| Quoted SCP reference material and declaration excerpts, where present | Original authors retain ownership; the pinned upstream README declares CC BY-SA 3.0 for the game/source. No Apache relicensing is claimed |
| NuGet packages, .NET, SDL3, libsndfile and codec libraries | Their own licenses, not this project's Apache grant |

Research-based reimplementation of behavior is distinct from copying an upstream implementation. The existing project documentation describes an independent implementation. The release audit found no vendored original Blitz3D/SCP source tree; it is not a forensic proof of the provenance of every statement or excerpt. Any subsequently identified upstream code must retain its attribution and license and be reviewed before distribution.

## Distribution boundaries

The source checkout does not include FMOD proprietary binaries, original Blitz3D executables, SCP game sources or protected game recordings/assets. Locally downloaded research material and build products are excluded from version control. Evidence reports may contain paths, signatures, hashes and observed output referring to these materials; that does not grant rights to redistribute them.

When distributing compiled applications or release archives, inventory what is actually included. Preserve dependency notices, including Roslyn's own third-party notices. Bundling SDL3, libsndfile, codec libraries, a .NET runtime or any other native library introduces the obligations of those exact builds. Dynamic use of LGPL libsndfile is not permission to omit its notices, source/relinking rights or other applicable conditions. No pre-cleared binary bundle is provided by this documentation.

Consult [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the [release audit](docs/public-release-audit.md) before packaging. License compliance and title to all contributions cannot be established solely by an automated scan.
