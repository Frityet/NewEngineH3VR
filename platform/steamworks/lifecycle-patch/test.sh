#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
out=build/steamworks/lifecycle/fixture
dotnet build platform/steamworks/lifecycle-fixture/Fixture.csproj -c Release -o "$out"
dotnet run --project platform/steamworks/lifecycle-patch/Patch.csproj -c Release -- "$out/Fixture.dll" "$out/Fixture.patched.dll"
cp "$out/Fixture.patched.dll" "$out/Fixture.dll"
dotnet "$out/Fixture.dll"
