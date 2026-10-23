#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
managed="${1:-$root/build/composed/final}"
project="$root/platform/probe-unity"
output="$root/build/platform-probe/NativeH3VRPlatformProbe.app"
mkdir -p "$project/Assets/Plugins" "$root/build/platform-probe"
cp "$managed/"*.dll "$project/Assets/Plugins/"
cp "$managed/"*.dylib "$project/Assets/Plugins/"
cp build/video/managed/NativeH3VR.Platform.dll "$project/Assets/Plugins/"
cp build/steamworks/*.dylib build/fmod/*.dylib "$project/Assets/Plugins/"
NATIVEH3VR_BUILD_OUTPUT="$output" /Applications/Unity/Unity.app/Contents/MacOS/Unity -batchmode -nographics -disable-assembly-updater -projectPath "$project" -buildTarget OSXUniversal -executeMethod BuildPlatformProbe.Build -logFile "$root/build/platform-probe-build.log"
codesign --force --deep --sign - "$output"
player=$(/usr/libexec/PlistBuddy -c 'Print CFBundleExecutable' "$output/Contents/Info.plist")
cd "$root/build/steamworks"
SteamAppId=450540 SteamGameId=450540 NATIVEH3VR_BANKS="$HOME/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets" NATIVEH3VR_PROBE_RESULT="$root/build/platform-probe/result.json" "$output/Contents/MacOS/$player" -force-metal -screen-width 320 -screen-height 240 -logFile "$root/build/platform-probe/player.log"
cat "$root/build/platform-probe/result.json"
