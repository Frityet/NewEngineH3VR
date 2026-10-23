#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
input="${1:-audit-managed/ported/NativeH3VR.Game.dll}"
dotnet run --project platform/video/patch/Patch.csproj -c Release -- prepare "$input" build/video/prepared/NativeH3VR.Game.dll
unity_managed="$PWD/.tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed"
dotnet build platform/video/backend/NativeH3VR.Platform.csproj -c Release -p:UnityManaged="$unity_managed" -p:GameManaged="$PWD/build/video/prepared/NativeH3VR.Game.dll"
cp platform/video/backend/bin/Release/netstandard2.0/NativeH3VR.Platform.dll build/video/managed/
dotnet run --project platform/video/patch/Patch.csproj -c Release -- finish build/video/prepared/NativeH3VR.Game.dll build/video/managed/NativeH3VR.Game.patched.dll build/video/managed/NativeH3VR.Platform.dll
dotnet run --project platform/video/patch/Patch.csproj -c Release -- normalize-self build/video/managed/NativeH3VR.Game.patched.dll build/video/managed/NativeH3VR.Game.dll
