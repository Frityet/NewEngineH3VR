#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
sdk="$PWD/.tools/fmod/sdk-2.03.15/api"
out="$PWD/build/audio-spatial"
mkdir -p "$out"
clang++ -arch arm64 -std=c++17 -O2 -dynamiclib -mmacosx-version-min=11.0 \
  -I"$sdk/core/inc" -I"$sdk/studio/inc" platform/fmod/bridge.cpp platform/audio-spatial/fmod-output.cpp \
  -L"$PWD/build/fmod" -lfmod -lfmodstudio -Wl,-reexport_library,"$PWD/build/fmod/libfmod.dylib" -Wl,-reexport_library,"$PWD/build/fmod/libfmodstudio.dylib" \
  -Wl,-rpath,@loader_path -Wl,-install_name,@rpath/libNativeH3VRFMOD.dylib -o "$out/libNativeH3VRFMOD.dylib"
codesign --force --sign - "$out/libNativeH3VRFMOD.dylib"
