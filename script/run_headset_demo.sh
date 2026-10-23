#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT_DIR/dist/NewEngineH3VR.app"
if [[ $# -gt 1 || ($# -eq 1 && "$1" != "--check") ]]; then
  echo "usage: $0 [--check]" >&2
  exit 2
fi
python3 "$ROOT_DIR/script/check_headset.py"
if [[ "${1:-}" == "--check" ]]; then exit 0; fi
if [[ ! -x "$APP/Contents/MacOS/NewEngineH3VR" ]]; then
  echo "Build NewEngineH3VR first: ./script/build_and_run.sh --build-only full" >&2
  exit 1
fi
mkdir -p "$ROOT_DIR/build/headset-demo"
# A demo is an interactive live session. Never inherit an acceptance probe's
# automatic controls, screenshots, private runtime or timed application exit.
for name in ${!NATIVEH3VR_@}; do unset "$name"; done
unset VR_OPENXR_SIMULATE VR_OPENXR_HEADLESS VI_HOME_CONTROL_DIR VR_OPENXR_CAPTURE
unset XR_RUNTIME_JSON
unset OPTIH3VR_NEW_ENGINE_TEST_ASSEMBLY OPTIH3VR_NEW_ENGINE_TEST_OUTPUT OPTIH3VR_NEW_ENGINE_COVER_TEST
unset NEWENGINEH3VR_GRAPHICS_RECREATE_TEST NEWENGINEH3VR_GRAPHICS_TEST_OUTPUT
for name in ${!NEWENGINEH3VR_SCOPE_@}; do unset "$name"; done
export NATIVEH3VR_OPENXR=live
export NATIVEH3VR_HEADSET_DEMO=1
NATIVEH3VR_RUN_ID="headset-demo-$(date +%Y%m%d-%H%M%S)"
export NATIVEH3VR_RUN_ID
echo "Opening NewEngineH3VR in the indoor range with live Index tracking. Log: build/headset-demo/player.log"
exec "$APP/Contents/MacOS/NewEngineH3VR" -force-metal -logFile "$ROOT_DIR/build/headset-demo/player.log"
