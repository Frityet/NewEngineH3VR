#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
dmg="${1:-$PWD/.tools/fmod/fmodstudioapi20315mac-installer.dmg}"
expected=85c3a2ba5d4cfd724a84fdebe98f71a44e0325c80132e0d1e42ab4afa4dcf24b
actual="$(shasum -a 256 "$dmg" | cut -d ' ' -f 1)"
if [[ "$actual" != "$expected" ]]; then
  echo 'Unexpected SDK hash: inspect the SDK version before changing the pinned input.' >&2
  exit 1
fi
sdk="$PWD/.tools/fmod/sdk-2.03.15"
if [[ ! -d "$sdk/api" ]]; then
  hdiutil attach -readonly -nobrowse "$dmg"
  mkdir -p "$sdk"
  cp -R '/Volumes/FMOD Programmers API Mac/FMOD Programmers API/api' "$sdk/"
  hdiutil detach '/Volumes/FMOD Programmers API Mac'
fi
mkdir -p .tools/fmod/source build/fmod
cp "$sdk/api/core/inc/"fmod*.cs .tools/fmod/source/
cp "$sdk/api/studio/inc/fmod_studio.cs" .tools/fmod/source/
cp "$sdk/api/core/lib/libfmod.dylib" build/fmod/
cp "$sdk/api/studio/lib/libfmodstudio.dylib" build/fmod/
install_name_tool -add_rpath @loader_path build/fmod/libfmodstudio.dylib
# Sign only these copied development libraries; the vendor SDK remains unmodified.
for lib in build/fmod/libfmod.dylib build/fmod/libfmodstudio.dylib; do
  if xattr -p com.apple.quarantine "$lib" >/dev/null 2>&1; then xattr -d com.apple.quarantine "$lib"; fi
  codesign --force --sign - "$lib"
done
