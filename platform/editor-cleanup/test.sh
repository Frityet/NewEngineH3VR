#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
out=build/editor-cleanup/fixture
dotnet build platform/editor-cleanup/fixture/Fixture.csproj -c Release -o "$out"
dotnet "$out/Fixture.dll"
dotnet run --project platform/editor-cleanup/patch/Patch.csproj -c Release -- "$out/Fixture.dll" "$out/Fixture.patched.dll"
cp "$out/Fixture.patched.dll" "$out/Fixture.dll"
dotnet "$out/Fixture.dll" patched
