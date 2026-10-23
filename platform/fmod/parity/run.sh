#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
mkdir -p build/fmod/parity/native
cp build/audio-spatial/libNativeH3VRFMOD.dylib build/fmod/parity/native/
ln -sf ../../libfmod.dylib build/fmod/parity/native/libfmod.dylib
ln -sf ../../libfmodstudio.dylib build/fmod/parity/native/libfmodstudio.dylib
dotnet run --project platform/fmod/call-audit/Audit.csproj -c Release
dotnet run --project platform/fmod/parity/Parity.csproj -c Release
