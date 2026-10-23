#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT_DIR/script/unity_toolchain.sh"
MODE="${1:-run}"
SCOPE="${2:-full}"
PROJECT="$ROOT_DIR/unity"
BUILD_METHOD="NativePortBuild.BuildBoot"
if [[ "$SCOPE" == "full" ]]; then
  PROJECT="$ROOT_DIR/unity-full"
  BUILD_METHOD="NativePortBuild.BuildFull"
elif [[ "$SCOPE" != "boot" ]]; then
  echo "Second argument must be boot or full." >&2
  exit 2
fi
APP_BUNDLE="$ROOT_DIR/dist/NewEngineH3VR.app"
APP_BINARY="$APP_BUNDLE/Contents/MacOS/NewEngineH3VRPlayer"
if [[ ! -f "$PROJECT/Assets/Plugins/NativeH3VR.Game.dll" ]]; then
  echo "Recovered project is not prepared. See docs/SETUP.md." >&2
  exit 1
fi
case "$MODE" in
run | --verify | --debug | --logs | --build-only) ;;
*)
  echo "usage: $0 [--build-only|--verify|--debug|--logs] [boot|full]" >&2
  exit 2
  ;;
esac
/usr/bin/pkill -f "^$APP_BINARY( |$)" 2>/dev/null || true
mkdir -p "$ROOT_DIR/build" "$ROOT_DIR/dist" "$PROJECT/Assets/Editor" "$PROJECT/Assets/NativePort"
python3 "$ROOT_DIR/script/stage_editor_tools.py" --project "$PROJECT"
cp "$ROOT_DIR/script/NativePortDiagnostics.cs" "$PROJECT/Assets/NativePort/NativePortDiagnostics.cs"
if [[ "$SCOPE" == "full" ]]; then
  python3 "$ROOT_DIR/script/verify_native_bundles.py" \
    --directory "$PROJECT/Assets/StreamingAssets" --project "$PROJECT" \
    --plan "$ROOT_DIR/build/native-bundle-plan.json"
fi
export NATIVEH3VR_BUILD_OUTPUT="$APP_BUNDLE"
"$UNITY_EDITOR" "${UNITY_LOCAL_ARGS[@]}" -batchmode -force-metal -disable-assembly-updater -projectPath "$PROJECT" -buildTarget OSXUniversal \
  -executeMethod "$BUILD_METHOD" -logFile "$ROOT_DIR/build/native-build.log"
python3 "$ROOT_DIR/shaders/verify_shader_proof_logs.py" \
  --output "$ROOT_DIR/build/native-player-compiler-verification.json" "$ROOT_DIR/build/native-build.log"
if [[ "$SCOPE" == "full" ]]; then
  "$ROOT_DIR/audit-assets/.venv/bin/python" "$ROOT_DIR/platform/lighting/restore_player_gi.py" \
    --player-data "$APP_BUNDLE/Contents/Resources/Data" \
    --report "$ROOT_DIR/build/lighting/full-player-gi.json" --apply
  "$ROOT_DIR/audit-assets/.venv/bin/python" "$ROOT_DIR/script/verify_player_scene_scripts.py" \
    --player-data "$APP_BUNDLE/Contents/Resources/Data" --project "$PROJECT" --require-full \
    --report "$ROOT_DIR/build/scene-integrity/full-player-script-proof.json"
  python3 "$ROOT_DIR/script/verify_native_bundles.py" \
    --directory "$APP_BUNDLE/Contents/Resources/Data/StreamingAssets" --project "$PROJECT" \
    --plan "$ROOT_DIR/build/native-bundle-plan.json"
  # Keep the verified Unity build as the recorded baseline. The separate
  # app-only receipt proves the two legacy PIP render-state corrections.
  "$ROOT_DIR/audit-assets/.venv/bin/python" "$ROOT_DIR/script/install_shader_state_overlay.py" \
    --app "$APP_BUNDLE" --before-package \
    --receipt "$ROOT_DIR/build/new-engine-depth-clip/overlay-$(date -u +%Y%m%dT%H%M%SZ)-$$"
fi
python3 "$ROOT_DIR/script/package_native.py" "$APP_BUNDLE"
python3 "$ROOT_DIR/script/install_optih3vr.py" --unity-contents "$(dirname "$(dirname "$UNITY_EDITOR")")"
[[ "$(/usr/bin/lipo -archs "$APP_BINARY")" == "arm64" ]]
# The Run action matches opening the packaged app: use the user's native runtime.
# Automated verification selects its own private synthetic mode in run_native.py.
export NATIVEH3VR_OPENXR="${NATIVEH3VR_OPENXR:-live}"
case "$MODE" in
--build-only) exit 0 ;;
--debug) exec lldb -- "$APP_BUNDLE/Contents/MacOS/NewEngineH3VR" -force-metal ;;
--verify) exec python3 "$ROOT_DIR/script/run_native.py" --seconds 30 ;;
--logs)
  /usr/bin/open -n "$APP_BUNDLE" --args -force-metal -logFile "$ROOT_DIR/build/native-player.log"
  tail -F "$ROOT_DIR/build/native-player.log"
  ;;
*) /usr/bin/open -n "$APP_BUNDLE" --args -force-metal -logFile "$ROOT_DIR/build/native-player.log" ;;
esac
