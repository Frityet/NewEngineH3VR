#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
wrapper="$PWD/.tools/steamworks/CSteamworks"
out="$PWD/build/steamworks"
mkdir -p "$out"
steam_api="${NATIVEH3VR_STEAM_API:-$HOME/Library/Application Support/Steam/Steam.AppBundle/Steam/Contents/MacOS/Frameworks/Steam Helper.app/Contents/MacOS/libsteam_api.dylib}"
lipo -verify_arch arm64 "$steam_api"
cp "$steam_api" "$out/libsteam_api.dylib"
install_name_tool -id @rpath/libsteam_api.dylib "$out/libsteam_api.dylib"
codesign --force --sign - "$out/libsteam_api.dylib"
clang++ -arch arm64 -std=c++11 -O2 -dynamiclib -mmacosx-version-min=11.0 \
  -DPOSIX -DOSX -DCSTEAMWORKS_DLLEXPORT -DUSE_BREAKPAD_HANDLER -I"$wrapper/steam" -I"$wrapper/src" -I"$wrapper/wrapper" \
  "$wrapper/src/steam_api.cpp" platform/steamworks/legacy_api.cpp \
  -L"$out" -lsteam_api -Wl,-rpath,@loader_path -Wl,-install_name,@rpath/libCSteamworks.dylib \
  -o "$out/libCSteamworks.dylib"
codesign --force --sign - "$out/libCSteamworks.dylib"
clang++ -arch arm64 -std=c++11 -O2 -mmacosx-version-min=11.0 -DPOSIX -DOSX \
  -I"$wrapper/steam" -I"$wrapper/src" platform/steamworks/probe.cpp \
  -L"$out" -lCSteamworks -lsteam_api -Wl,-rpath,@loader_path -o "$out/steam-probe"
