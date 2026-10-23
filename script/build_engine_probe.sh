#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Unity.app/Contents/MacOS/Unity}"
mkdir -p "$ROOT_DIR/build" "$ROOT_DIR/dist"
export NATIVEH3VR_BUILD_OUTPUT="$ROOT_DIR/dist/NewEngineH3VR Engine Probe.app"
"$UNITY_EDITOR" -batchmode -nographics -projectPath "$ROOT_DIR/engine-probe" \
  -buildTarget OSXUniversal -executeMethod BuildNativeProbe.Build \
  -logFile "$ROOT_DIR/build/engine-probe-build.log"
file "$NATIVEH3VR_BUILD_OUTPUT/Contents/MacOS/NewEngineH3VR Engine Probe"
