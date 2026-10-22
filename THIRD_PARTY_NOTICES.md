# Third-party notices

BlitzCS was created and is led by Marcelo Buratti de Freitas Filho. That attribution covers project origin and leadership, not ownership of third-party work. This inventory was prepared against the M16 source checkpoint `216c025` and the restored package metadata; see [the exact package inventory](docs/evidence/public-release-packages.json).

## Managed dependencies

| Component | Version used | License / attribution |
| --- | --- | --- |
| .NET SDK/runtime | .NET 10; validation SDK 10.0.401 | MIT for core .NET projects; Microsoft, .NET Foundation and contributors. Runtime distributions have additional third-party notices |
| Microsoft.CodeAnalysis.CSharp and Common | 5.0.0 | MIT; Microsoft Corporation |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | MIT; Microsoft Corporation |
| Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage, Microsoft.TestPlatform.ObjectModel, Microsoft.TestPlatform.TestHost | 17.14.1 | MIT; Microsoft Corporation |
| Newtonsoft.Json (test dependency) | 13.0.3 | MIT; James Newton-King |
| xunit, xunit.assert, xunit.core, xunit.extensibility.core, xunit.extensibility.execution | 2.9.3 | Apache-2.0; .NET Foundation and contributors |
| xunit.abstractions | 2.0.3 | Apache-2.0; upstream license URL in package metadata |
| xunit.analyzers | 1.18.0 | Apache-2.0; .NET Foundation and contributors |
| xunit.runner.visualstudio | 3.1.4 and 3.1.5, depending on test project | Apache-2.0; .NET Foundation and contributors |

License copies: [.NET](licenses/dotnet-MIT.txt), [Roslyn](licenses/roslyn-MIT.txt), [VSTest](licenses/vstest-MIT.txt), [Newtonsoft.Json](licenses/newtonsoft-json-MIT.txt), [xUnit notices](licenses/xunit-Apache-2.0.txt), and the full [Apache-2.0 text](LICENSE). Roslyn's packaged [ThirdPartyNotices.rtf](licenses/roslyn-ThirdPartyNotices.rtf) is retained unmodified. These copies supplement, rather than replace, notices in the exact package/runtime distribution.

Primary sources: [Roslyn](https://github.com/dotnet/roslyn), [.NET runtime](https://github.com/dotnet/runtime), [VSTest](https://github.com/microsoft/vstest), [Newtonsoft.Json 13.0.3](https://github.com/JamesNK/Newtonsoft.Json/tree/13.0.3), [xUnit 2.9.3 license](https://github.com/xunit/xunit/blob/v2-2.9.3/license.txt). Packages are restored from NuGet; their implementation binaries are not tracked in this source repository.

## Audio runtime libraries

- **SDL3**: zlib license, SDL contributors. The verified system library is SDL 3.4.18. Preserve the [installed license text](licenses/SDL3-zlib.txt) when redistributing it. [Official licensing](https://www.libsdl.org/license.php).
- **libsndfile**: the core library uses the GNU Lesser General Public License, version 2.1 or optionally version 3; the verified system library is 1.2.2. Copies of [LGPL 2.1](licenses/libsndfile-COPYING.txt), [LGPL 3](licenses/LGPL-3.0.txt) and its accompanying [GPL 3](licenses/GPL-3.0.txt) are provided. Copyright belongs to Erik de Castro Lopo and other libsndfile contributors. [Official licensing and redistribution guidance](https://libsndfile.github.io/libsndfile/FAQ.html).
- **Codec dependencies**: the decoder uses system libsndfile, whose enabled codecs and transitive libraries depend on the installed build. Ogg/Vorbis and FLAC may be involved; they are not vendored here. A binary distributor must inventory the actual native dependency closure, preserve each license and comply with the LGPL obligations for its exact build. No universal codec bundle is licensed by this notice.

The native libraries are dynamically loaded from the host; their binaries are not committed. Apache-2.0 on Blitz3DCS.Audio does not relicense them. FFmpeg/libvorbis was used as an external tool to create the synthetic Vorbis fixture, not incorporated as source or distributed as a binary here.

## Blitz3D reference implementation

The original Blitz3D compiler/runtime was consulted and executed for compatibility validation. It is a separate work by **Blitz Research Ltd** and its contributors, including original creator **Mark Sibly**. The inspected source declares:

> Copyright (c) 2013 Blitz Research Ltd

It is distributed under the zlib/libpng license. The original notice and build text are preserved in [licenses/blitz3d-zlib.txt](licenses/blitz3d-zlib.txt), from [the inspected upstream commit](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/LICENSE.TXT). No original compiler/runtime executable or full upstream source tree is redistributed here. Any reference excerpts retain their original attribution and terms; BlitzCS must not be represented as the original Blitz3D software.

## SCP: Containment Breach reference project

The separately obtained original source is used for compatibility auditing. [The pinned upstream README](https://github.com/Regalis11/scpcb/blob/edb8fe0840b78f14d1aef3a0bf6174630e7be296/README.md) identifies the game and source as **Creative Commons Attribution-ShareAlike 3.0** and acknowledges the SCP Foundation community. Attribution belongs to Regalis/Regalis11, the SCP: Containment Breach contributors and the respective SCP authors; upstream credits and asset-specific terms remain relevant.

No complete game source, game recording, graphics, FMOD binary or game asset archive is tracked here. Research reports contain paths, hashes, API declarations and limited reference excerpts; those excerpts retain upstream terms, including ShareAlike where applicable. See the [CC BY-SA 3.0 legal text](licenses/CC-BY-SA-3.0.txt). This notice does not purport to clear redistribution of every file in the original game's distribution. A genuine game audio file used locally in M15 was not committed; its hash in a report is not the asset.

## FMOD

FMOD 3.75 is proprietary software by Firelight Technologies. The original Win32 library was executed in a separate x86 process via Proton solely as a semantic reference. It is not the implementation behind the SDL3 compatibility layer and is not included in this repository or licensed by Apache-2.0. Historical free/non-commercial availability must not be interpreted as a general redistribution license. Obtain any original library and permission separately from its rights holder. [FMOD licensing](https://www.fmod.com/licensing).

## Project fixtures and tools

`tests/Blitz3DCS.Audio.Tests/Fixtures/generated-tone.ogg` is an original synthetic sine tone, with an existing **CC0-1.0** dedication in its [README](tests/Blitz3DCS.Audio.Tests/Fixtures/README.md); the [full legal text](licenses/CC0-1.0.txt) is supplied. `docs/evidence/m15-fmod/tone.wav` is another project-generated synthetic tone, covered by the original-project Apache-2.0 scope. Neither is a recording taken from SCP.

Trail-Code is optional external developer tooling, not a compiler/runtime dependency or vendored implementation. Wine/Proton and original SDKs are optional validation tools, not bundled products. Their respective licenses govern separate installations. Repository instructions and research logs do not grant a license to redistribute these tools.

## Distribution review

Do not remove upstream notices, replace third-party copyright with the project creator's name, or assume that an Apache-licensed source checkout clears a self-contained binary release. Review [LICENSING.md](LICENSING.md) and [the publication audit](docs/public-release-audit.md) before packaging dependencies or adding external source/assets.
