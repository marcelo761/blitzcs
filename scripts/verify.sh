#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build BlitzCS.slnx --configuration Release --nologo
dotnet test BlitzCS.slnx --configuration Release --no-build --nologo
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Legacy/hello.bb -o artifacts/hello.dll
dotnet artifacts/hello.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/answer.bb -o artifacts/answer.dll
dotnet artifacts/answer.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/typed.bb -o artifacts/typed.dll
dotnet artifacts/typed.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/legacy-loops.bb -o artifacts/legacy-loops.dll
dotnet artifacts/legacy-loops.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Legacy/arrays.bb -o artifacts/arrays.dll
dotnet artifacts/arrays.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/objects.bb -o artifacts/objects.dll
dotnet artifacts/objects.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/MultiFile/main.bb -o artifacts/multifile.dll
dotnet artifacts/multifile.dll
python3 -m unittest discover -s tests/differential -v
python3 tools/differential.py
python3 tools/differential.py --manifest tests/Compatibility/objects.json --report artifacts/object-differential-local.json
python3 tools/differential.py --manifest tests/Compatibility/lifetime.json --report artifacts/lifetime-differential-local.json
python3 tools/differential.py --manifest tests/Compatibility/multifile.json --report artifacts/multifile-differential-local.json
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/FixedVectors/main.bb -o artifacts/fixed-vectors.dll
dotnet artifacts/fixed-vectors.dll
python3 tools/differential.py --manifest tests/Compatibility/vectors.json --report artifacts/vector-differential-local.json
python3 tools/differential.py --manifest tests/Compatibility/constants.json --report artifacts/constant-differential-local.json
python3 tools/differential.py --manifest tests/Compatibility/milestone9.json --report artifacts/m9-local-differential.json
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Native/runtime-error.bb -o artifacts/native-runtime-error.dll --externals samples/Native/runtime.json
if dotnet artifacts/native-runtime-error.dll > artifacts/native-runtime-error.stdout 2> artifacts/native-runtime-error.stderr; then
  echo "RuntimeError unexpectedly succeeded" >&2
  exit 1
fi
rg -q '^before native failure$' artifacts/native-runtime-error.stdout
rg -q 'native runtime demonstration' artifacts/native-runtime-error.stderr
python3 tools/differential.py --manifest tests/Compatibility/handles.json --report artifacts/m10-handles-local.json
python3 tools/differential.py --manifest tests/Compatibility/numeric.json --report artifacts/m10-numeric-local.json
python3 tools/differential.py --manifest tests/Compatibility/controls.json --report artifacts/m10-controls-local.json
python3 tools/differential.py --manifest tests/Compatibility/data.json --report artifacts/m10-data-local.json
python3 tools/differential.py --manifest tests/Compatibility/milestone11.json --report artifacts/m11-local-differential.json
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/Handles/main.bb -o artifacts/hybrid-handles.dll
dotnet artifacts/hybrid-handles.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Diagnostics/handle-memory.bb -o artifacts/handle-memory.dll
dotnet artifacts/handle-memory.dll > artifacts/handle-memory.json
python3 tools/prepare_native_fixtures.py artifacts/m12-assets
env -u BLITZCS_M12_MISSING BLITZCS_M12_ROOT="$PWD/artifacts/m12-assets" python3 tools/differential.py --manifest tests/Compatibility/milestone12.json --externals samples/Native/blitz3dcs.json --report artifacts/m12-native-local.json
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Hybrid/NativeFiles/main.bb -o artifacts/native-files-hybrid.dll --externals samples/Native/blitz3dcs.json
dotnet artifacts/native-files-hybrid.dll
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/NativeInterop/main.bb -o artifacts/native-interop.dll --externals samples/NativeInterop/native.json
env -u BLITZCS_NATIVEINTEROP_LIBRARY dotnet artifacts/native-interop.dll > artifacts/native-interop.stdout
python3 - <<'PY'
from pathlib import Path
assert Path('artifacts/native-interop.stdout').read_text().splitlines() == ['42', '3.5', '9', 'native:Olá 🌱', '-1', '42', '1']
PY
# M14: real SDL3 lifecycle; dummy is explicitly headless and emits no sound.
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Audio/main.bb -o artifacts/audio/main.dll --externals samples/Audio/scp.json
env SDL_AUDIODRIVER=dummy dotnet artifacts/audio/main.dll > artifacts/audio/stdout.txt
python3 - <<'PY'
from pathlib import Path
assert Path('artifacts/audio/stdout.txt').read_text().splitlines() == ['1', '1']
PY
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- check-semantics samples/Audio/declared-only.bb --externals samples/Audio/declarations.json
if dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Audio/declared-only.bb -o artifacts/audio/unbound.dll --externals samples/Audio/declarations.json > artifacts/audio/unbound.stdout 2> artifacts/audio/unbound.stderr; then
  echo "Declaration-only audio call unexpectedly emitted an assembly" >&2
  exit 1
fi
rg -q 'declared-only.bb\(2,7\).*no implementation' artifacts/audio/unbound.stderr
python3 tools/compare_audio_profile.py
# M15: functional playback and objective PCM from SDL's disk driver.
# The synthetic CC0 Vorbis asset keeps this reproducible without downloading game assets.
python3 tools/probe_audio_playback.py --asset tests/Blitz3DCS.Audio.Tests/Fixtures/generated-tone.ogg --capture-disk --report artifacts/m15-playback.json
python3 tools/compare_fmod_m15.py --report artifacts/m15-fmod-comparison.json
# M16: actual host query and explicit missing-window capability, never a fabricated HWND.
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Native/system-property.bb -o artifacts/system-property.dll --externals samples/Native/blitz3dcs.json
dotnet artifacts/system-property.dll > artifacts/system-property.stdout
python3 - <<'PY'
from pathlib import Path
assert Path('artifacts/system-property.stdout').read_text().splitlines() == [
    'unknown-empty=1', 'cpu-case-insensitive=1', 'tempdir-nonempty=1', 'tempdir-trailing-separator=1']
PY
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Native/system-property-unsupported.bb -o artifacts/system-property-unsupported.dll --externals samples/Native/blitz3dcs.json
if dotnet artifacts/system-property-unsupported.dll > artifacts/system-property-unsupported.stdout 2> artifacts/system-property-unsupported.stderr; then
  echo "SystemProperty unexpectedly fabricated a native window handle" >&2
  exit 1
fi
rg -q 'PlatformNotSupportedException.*AppHWND' artifacts/system-property-unsupported.stderr
rg -q 'system-property-unsupported.bb:line 1' artifacts/system-property-unsupported.stderr
mkdir -p artifacts/m17
# M17: typed graphics contracts are declarations, not executable engine bindings.
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- check-semantics samples/LegacyGraphics/declared-only.bb --externals samples/LegacyGraphics/contracts.json
if dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/LegacyGraphics/declared-only.bb -o artifacts/m17/graphics-unbound.dll --externals samples/LegacyGraphics/contracts.json > artifacts/m17/graphics-unbound.stdout 2> artifacts/m17/graphics-unbound.stderr; then
  echo "Graphics declarations unexpectedly emitted an assembly without a runtime" >&2
  exit 1
fi
rg -q 'declared-only.bb\(3,7\).*no implementation' artifacts/m17/graphics-unbound.stderr
test ! -f artifacts/m17/graphics-unbound.dll
# DebugLog is a real void command, with an explicitly attached diagnostic sink.
dotnet run --project src/BlitzCS.CLI --configuration Release --no-build -- build samples/Native/debug-log.bb -o artifacts/m17/debug-log.dll --externals samples/Native/blitz3dcs.json
dotnet artifacts/m17/debug-log.dll > artifacts/m17/debug-log.stdout 2> artifacts/m17/debug-log.stderr
python3 - <<'PY'
from pathlib import Path
assert Path('artifacts/m17/debug-log.stdout').read_text().splitlines() == ['2']
assert Path('artifacts/m17/debug-log.stderr').read_text().splitlines() == ['from C#', '[from BASIC]']
PY
python3 tools/differential.py --manifest tests/Compatibility/milestone17.json --externals samples/Audio/scp.json --report artifacts/m17/local-differential.json
python3 tools/compare_m17_evidence.py
