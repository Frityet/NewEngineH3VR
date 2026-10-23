#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p build/ugui/runtime build/ugui/composed
dotnet build platform/ugui/runtime/UnityEngine.UI.csproj -c Release -o build/ugui/runtime
python3 platform/ugui/provenance.py
cp "${1:-build/composed/final}/"*.dll build/ugui/composed/
cp build/ugui/runtime/UnityEngine.UI.dll build/ugui/composed/
dotnet run --project platform/ugui/UGUI.csproj -c Release
dotnet run --project audit-managed/verify/Verify.csproj -c Release -- build/ugui/composed
