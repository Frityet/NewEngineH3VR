#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mono=/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/bin
mkdir -p build/optional-hardware/native-fixture
python3 platform/optional-hardware/fixture/make_fixture.py
"$mono/mcs" -out:build/optional-hardware/fixture-original.exe platform/optional-hardware/fixture/Fixture.cs
dotnet run --project platform/optional-hardware/patch/Patch.csproj -- build/optional-hardware/fixture-original.exe build/optional-hardware/fixture-patched.exe >build/optional-hardware/fixture-patch.log
for variant in original patched; do
  env -u DYLD_LIBRARY_PATH FORCETUBE_FIXTURE_UNAVAILABLE=1 MONO_LOG_LEVEL=debug MONO_LOG_MASK=dll "$mono/mono" "build/optional-hardware/fixture-$variant.exe" >"build/optional-hardware/$variant-unavailable.log" 2>&1
done
clang -arch arm64 -dynamiclib platform/optional-hardware/fixture/native.c -o build/optional-hardware/native-fixture/libForceTubeVR_API_x64.dylib
FORCETUBE_FIXTURE_UNAVAILABLE=0 DYLD_LIBRARY_PATH="$PWD/build/optional-hardware/native-fixture" MONO_LOG_LEVEL=debug MONO_LOG_MASK=dll "$mono/mono" build/optional-hardware/fixture-patched.exe >build/optional-hardware/patched-supported.log 2>&1
python3 - <<'PY'
from pathlib import Path
import json
folder=Path('build/optional-hardware')
old=(folder/'original-unavailable.log').read_text()
new=(folder/'patched-unavailable.log').read_text()
supported=(folder/'patched-supported.log').read_text()
assert 'DllImport attempting to load' in old, 'Fixture failed to reproduce original eager native resolution'
assert 'PASS unavailable' in old and 'PASS unavailable' in new
assert 'DllImport' not in new, 'Unavailable path still resolves a native library'
assert 'PASS supported' in supported, 'Supported branch did not preserve native calls/results'
result={'originalUnavailableImportAttempts':old.count('DllImport attempting to load'), 'patchedUnavailableImportAttempts':new.count('DllImport attempting to load'),'unavailableQueriesThrow':True,'supportedSevenNativeCallsAndArgumentsPass':True,'physicalForceTubeValidated':False}
(folder/'fixture-result.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result,indent=2))
PY
