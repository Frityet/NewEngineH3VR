#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT_DIR/script/unity_toolchain.sh"
PROJECT="$ROOT_DIR/unity-full"
mkdir -p "$PROJECT/Assets/Editor" "$ROOT_DIR/build"
python3 "$ROOT_DIR/script/stage_editor_tools.py" --project "$PROJECT"
export NATIVEH3VR_BUNDLE_PLAN="$ROOT_DIR/build/native-bundle-plan.json"
export NATIVEH3VR_BUNDLE_OUTPUT="$ROOT_DIR/build/StreamingAssets"
# ShaderVariantCollection validates against the Editor's active graphics device.
# Null graphics rejects supported original Deferred passes before the build.
"$UNITY_EDITOR" "${UNITY_LOCAL_ARGS[@]}" -batchmode -force-metal -disable-assembly-updater -projectPath "$PROJECT" -buildTarget OSXUniversal \
  -executeMethod NativeBundleBuild.Build -logFile "$ROOT_DIR/build/native-bundles-build.log"
python3 "$ROOT_DIR/shaders/verify_shader_proof_logs.py" \
  --output "$ROOT_DIR/build/native-bundles-compiler-verification.json" "$ROOT_DIR/build/native-bundles-build.log"
python3 "$ROOT_DIR/script/stage_native_bundles.py"
