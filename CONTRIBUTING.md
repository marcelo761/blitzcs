# Contributing to BlitzCS

BlitzCS is created and led by Marcelo Buratti de Freitas Filho. Contributions should preserve its engine-independent architecture and be small enough to review with evidence.

## Development setup

Install the .NET 10 SDK, restore `BlitzCS.slnx`, and follow the [README](README.md). Full verification also requires Python 3, a C compiler, SDL3 and libsndfile on the verified Linux platform. No original game assets or proprietary SDK are required for the ordinary regression suite.

```bash
dotnet build BlitzCS.slnx -c Release --nologo
dotnet test BlitzCS.slnx -c Release --no-build --nologo
dotnet build BlitzCS.slnx -c Debug --nologo
dotnet test BlitzCS.slnx -c Debug --no-build --nologo
python3 -m unittest discover -s tests/differential -v
bash scripts/verify.sh
```

Use a topic branch. Keep formatting/refactoring separate from behavior changes and use descriptive conventional commits. Preserve existing history and milestone tags; never force-push shared history. Before submission, inspect `git diff --check`, the staged diff and every included file.

## Compatibility changes

Add focused positive and negative tests. For legacy semantic changes, cite original parser/runtime behavior and run a differential probe when the original environment is available. Clearly distinguish local expectations, replayed captures and fresh original execution. Record unavailable comparisons as pending; do not fabricate results or add silent runtime stubs.

Keep `BlitzCS.Compiler` independent of engine-specific APIs. Put Blitz3D-specific services in Blitz3DCS components and use typed external registration. Preserve BASIC/C#/native interoperability, source diagnostics, managed object lifetime and resource ownership. Public contracts and deliberate divergences need documentation.

For repository context, follow [AGENTS.md](AGENTS.md). Trail-Code is optional developer tooling; `trail map`, `trail ask "symbol" --source` and `trail build` help navigate and refresh the local graph. Do not commit its cache.

## Licensing and attribution

Submit only work you have the right to contribute under the project's [licensing scope](LICENSING.md). Unless explicitly agreed otherwise, intentionally submitted original contributions use Apache-2.0. Identify all third-party code and retain its copyright, license and provenance; update `THIRD_PARTY_NOTICES.md` when necessary. A contribution does not transfer its author's copyright.

Do not add proprietary FMOD binaries, SCP assets/source snapshots, credentials, personal files or downloaded SDKs. Synthetic fixtures must have a clear creation method and license. Separate third-party licenses are not automatically replaced by the repository license.

## Reports and review

Describe the problem, reproduction, expected behavior, platform and SDK version, changed contracts, test commands and remaining uncertainty. Do not include secrets in logs. Once an official GitHub repository is configured, use its issues and pull requests; no destination is assumed in this checkout.

For a suspected vulnerability, avoid posting exploit details or credentials publicly. Use a private reporting channel if the official repository provides one; no private contact address has been designated here.
