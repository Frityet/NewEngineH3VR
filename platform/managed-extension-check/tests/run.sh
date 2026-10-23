#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
workspace="$(cd "$root/../.." && pwd)"
compiler="$workspace/.tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/MonoBleedingEdge/bin/mcs"
checker="$root/bin/Release/net10.0/ManagedExtensionCheck.dll"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/old" "$tmp/compatible" "$tmp/field" "$tmp/removed" "$tmp/removed-field"
"$compiler" -target:library -out:"$tmp/old/Plugin.dll" "$root/tests/old.cs"
"$compiler" -target:library -out:"$tmp/compatible/Plugin.dll" "$root/tests/compatible.cs"
"$compiler" -target:library -out:"$tmp/field/Plugin.dll" "$root/tests/changed_field.cs"
"$compiler" -target:library -out:"$tmp/removed/Plugin.dll" "$root/tests/removed_type.cs"
"$compiler" -target:library -out:"$tmp/removed-field/Plugin.dll" "$root/tests/removed_field.cs"

dotnet "$checker" "$tmp/old/Plugin.dll" "$tmp/compatible/Plugin.dll" >"$tmp/compatible.json"
python3 - "$tmp/compatible.json" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert r["passed"] is True, r
PY

if dotnet "$checker" "$tmp/old/Plugin.dll" "$tmp/field/Plugin.dll" >"$tmp/field.json"; then
  echo "field addition was incorrectly accepted" >&2
  exit 1
fi
python3 - "$tmp/field.json" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert r["passed"] is False and any("field" in x for x in r["failures"]), r
PY

if dotnet "$checker" "$tmp/old/Plugin.dll" "$tmp/removed-field/Plugin.dll" >"$tmp/removed-field.json"; then
  echo "field removal was incorrectly accepted" >&2
  exit 1
fi
python3 - "$tmp/removed-field.json" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert r["passed"] is False and any("field" in x for x in r["failures"]), r
PY

if dotnet "$checker" "$tmp/old/Plugin.dll" "$tmp/removed/Plugin.dll" >"$tmp/removed.json"; then
  echo "type removal was incorrectly accepted" >&2
  exit 1
fi
python3 - "$tmp/removed.json" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
assert r["passed"] is False and any("RemovedType" in x for x in r["failures"]), r
PY

echo "PASS managed extension contract checker fixtures"
