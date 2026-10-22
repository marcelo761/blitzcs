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
