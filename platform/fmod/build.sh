#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
sdk="$PWD/.tools/fmod/sdk-2.03.15/api"
out="$PWD/build/fmod"
clang++ -arch arm64 -std=c++17 -O2 -dynamiclib -mmacosx-version-min=11.0 \
  -I"$sdk/core/inc" -I"$sdk/studio/inc" platform/fmod/bridge.cpp platform/audio-spatial/fmod-output.cpp \
  -L"$out" -lfmod -lfmodstudio -Wl,-reexport_library,"$out/libfmod.dylib" -Wl,-reexport_library,"$out/libfmodstudio.dylib" \
  -Wl,-rpath,@loader_path -Wl,-install_name,@rpath/libNativeH3VRFMOD.dylib -o "$out/libNativeH3VRFMOD.dylib"
codesign --force --sign - "$out/libNativeH3VRFMOD.dylib"
