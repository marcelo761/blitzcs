# Public release preparation audit

## Scope and outcome

This documentation-only preparation starts from `216c025` (`blitzcs-m16-checkpoint`) on `codex/milestone-16`, with a clean working tree. A new `codex/public-release` branch preserves that ancestry. No compiler, runtime, test implementation, project dependency or milestone tag is changed. The requested commit message is `docs: prepare BlitzCS for public release`; resolve its object ID with `git log -1 --format='%H %s'` on the prepared branch.

No remote was configured at inspection or validation time. No repository was created and nothing was pushed. Publication requires the owner to identify the official GitHub destination. This audit is evidence for review, not certification of ownership of every historical line or a pre-cleared binary distribution.

## Authorship and licensing

The user identifies Marcelo Buratti de Freitas Filho as creator and project leader. `NOTICE` records that origin without assigning him ownership of upstream work. Existing Git authorship is preserved: the inspected commits identify `marcelo761` with a GitHub noreply email address. No commits were amended, squashed, recreated or deleted.

`LICENSE` contains the full official Apache License 2.0. `LICENSING.md` limits its scope to original contributions over which the project has licensing rights. Upstream notices, reference excerpts, dependencies and the existing CC0 Vorbis fixture keep their own terms. `THIRD_PARTY_NOTICES.md` and `licenses/` preserve the inspected license texts and ownership information.

The restored managed graph has 17 package/version entries, inventoried from `obj/project.assets.json` and each package's `.nuspec`. MIT applies to Roslyn, Microsoft test infrastructure and Newtonsoft.Json. Apache-2.0 applies to xUnit components; the older abstractions package points to its upstream license rather than an SPDX expression. .NET core uses MIT with additional notices in its distributions. SDL3 is zlib; libsndfile core is LGPL 2.1 or optionally 3. Original Blitz3D is zlib/libpng, not Apache. The pinned SCP README declares CC BY-SA 3.0 for game/source; this is not blanket clearance for third-party assets or binaries contained in the game distribution. FMOD is proprietary and excluded.

No full third-party implementation source tree or SDK header bundle was found among tracked files. Existing documentation describes the compiler as independently implemented using upstream behavior as reference. Reports contain API declarations, limited source references and captured observations; these are excluded from Apache relicensing where third-party rights apply. This audit does not prove independent origin of every implementation statement. Any future discovery of copied material needs specific provenance review and attribution before redistribution.

## Git history and content inspection

Before preparation, `git rev-list --objects --all` enumerated **629 reachable objects** and **429 historical paths**. The current baseline had **371 tracked paths**. All seven existing commits, branches and six milestone tags were retained. A separate `git cat-file --batch-all-objects` scan covered **850 locally stored objects**, including **556 blobs**, encompassing unreachable objects as well as ordinary branch/tag history.

The blob scan checked private-key markers, GitHub token formats, AWS access-key formats, credential-bearing HTTP URLs, and quoted long assignments to common password/token/API-key names. **No matching candidates were found.** This is a heuristic scan; it cannot establish that every possible credential format or arbitrary secret is absent. Git metadata and configuration were checked separately; no remote credentials were present.

The only binary blobs found in reachable history and in the all-stored-object scan were:

| Path | Bytes | Provenance |
| --- | ---: | --- |
| `docs/evidence/m15-fmod/tone.wav` | 132344 | Project-generated synthetic WAV, documented in the adjacent README |
| `tests/Blitz3DCS.Audio.Tests/Fixtures/generated-tone.ogg` | 3694 | Project-generated synthetic Vorbis tone, existing CC0 dedication |

No tracked or historical `.dll`, `.exe`, SDK/game archive, private-key file, FMOD library or game recording was identified. The locally used original FMOD DLL resides under ignored build/research locations. The locally downloaded SCP sound resides under `ignoredartifacts/`; that directory is now explicitly excluded in the shared `.gitignore`, rather than relying only on a developer's local exclusion.

Historical reports **do contain development-machine metadata**: local filesystem/user paths, process IDs, temporary paths and Pulse routing names such as `mini_eq_sink`. Git records the existing GitHub noreply author address. These are documented research metadata, not detected credentials. They have not been erased or hidden by rewriting history. The owner should review whether these disclosures are acceptable before public publication. Removing them only from a new commit would not remove them from preserved history.

`git fsck --full --no-reflogs` reported dangling trees and one dangling commit, without an integrity failure. Those local unreachable objects were also included in the content scan; ordinary pushes of a branch do not publish all unreachable objects. No pruning, reflog deletion or history repair was performed.

## Validation rerun

Verified on Linux x64 with SDK **10.0.401**, SDL3 **3.4.18** and libsndfile **1.2.2**:

| Check | Actual result |
| --- | --- |
| Release build | 0 warnings, 0 errors |
| Release .NET tests | 611 passed, 0 failed, 0 skipped |
| Debug build | 0 warnings, 0 errors |
| Debug .NET tests | 611 passed, 0 failed, 0 skipped |
| Python tests | 40 passed |
| Local language fixtures | 86 passed |
| FMOD profile replay | 34 matches, 8 documented divergences, 0 unexpected mismatches |
| Audio verification | Default/dummy/disk playback profiles passed; non-silent post-SDL PCM verified |
| Native and managed interoperability | Existing typed native, hybrid and dependency-publication tests passed |
| Whitespace review | Authored documents pass; verbatim upstream SDL3/libsndfile texts retain their original trailing whitespace |

Commands:

```bash
bash scripts/verify.sh
dotnet build BlitzCS.slnx -c Debug --nologo
dotnet test BlitzCS.slnx -c Debug --no-build --nologo
```

The full Release script includes the Release build/tests, Python tests, 86 local fixtures, examples, memory/handle stress, managed/native publication tests and audio profiles. Its expected negative `SystemProperty("AppHWND")` subprocess emits a runtime error and the shell may print `Aborted (core dumped)`; the script verifies that failure and exits successfully. This was not silently counted as a successful program execution.

No new run of the original Blitz3D or FMOD binary was performed for this documentation task. Existing genuine captures and the M16 result of 85/86 language comparisons remain historical evidence, not newly executed comparisons. There is no claim that local fixtures establish complete equivalence or that SDL PCM proves physical audibility.

Machine-readable counts, Git refs and validation-log hashes are in [public-release-validation.json](evidence/public-release-validation.json). Logs were generated locally as `/tmp/blitzcs-public-release.log` and `/tmp/blitzcs-public-debug.log`; they are not required build inputs and are not committed. Follow the commands above to reproduce rather than relying on those temporary paths surviving.

## Known product and distribution limits

The documentation records the current SCP blocker as `Windowed3D` at `fullscreen_window_fix.bb:47:8`, not the obsolete earlier `FSOUND_Init` blocker. Integrated syntax is validated; integrated semantic compilation, linking and execution are not complete. No source of the game was modified for publication.

No renderer or other feature was added. Native library distribution is platform-specific, and the source license does not clear transitive native-library bundles. A binary release needs its own exact dependency inventory and license compliance check. Review the intentionally retained local metadata and identify the official remote before publishing.

## Files intended for the preparation commit

- Updated: `README.md`, `.gitignore`.
- Added: `LICENSE`, `NOTICE`, `LICENSING.md`, `THIRD_PARTY_NOTICES.md`, `CONTRIBUTING.md`.
- Added: `licenses/` containing third-party license texts and retrieval provenance.
- Added: this audit, `docs/github-publication.md`, package inventory and validation evidence.

The staged file list contains 25 files. Authored documentation passes whitespace checks; the copied SDL3 and libsndfile license texts intentionally retain upstream whitespace and are byte-identical to the installed notices. The final staged file list and diff are reviewed before committing. No generated assemblies, caches, private research downloads, credentials or source implementation changes belong in this commit. The GitHub description and suggested topics are prepared in `docs/github-publication.md`; no account or destination is assumed.

## Official publication follow-up

After preparation, the owner explicitly identified `https://github.com/marcelo761/BlitzCS.git` as the official destination and requested publication. It was configured as `origin`. GitHub metadata confirmed owner `marcelo761`, public visibility and default branch `main`; `git ls-remote` returned no existing refs. No unrelated remote work required reconciliation.

Local `main` at `62c1dbb` was an ancestor of `codex/public-release` at `743acc3` (0 commits exclusive to main, 7 exclusive to the publication branch). `git merge --ff-only codex/public-release` advanced `main` without creating replacement commits, dropping history or changing the publication branch. All existing milestone branches and six tag object IDs were preserved.

The local metadata was reviewed again before publication. The research records contain 49 local-user-path occurrences, 22 mounted-path occurrences and 3 audio-routing-name occurrences across 30 documentation/research files. No email-bearing content was found in that scoped recheck; Git retains the existing GitHub noreply author address. These values are environment provenance, not authentication secrets. No correction to those historical observations is necessary, and changing only the current files would not remove earlier copies. No private credential, proprietary binary or protected game recording was identified that would require blocking this source publication. Heuristic scanning still cannot guarantee absence of all conceivable secrets.

This follow-up changes documentation only. The integrated source tree equals the previously validated publication checkpoint. The Release/Debug and other results above therefore remain applicable; no additional compiler/runtime change or test run is claimed. Network push results must be confirmed from the live remote refs, as described in `github-publication.md`.
