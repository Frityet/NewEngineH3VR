#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
project="$PWD/platform/video/serialization/probe"
mkdir -p "$project/Assets/Plugins" "$project/Assets/Recovered"
cp build/composed/final/*.dll "$project/Assets/Plugins/"
cp unity-full/Assets/Plugins/NativeH3VR.Game.dll.meta "$project/Assets/Plugins/"
cp unity-full/Assets/MonoBehaviour/Splash_MainMenu.asset* "$project/Assets/Recovered/"
python3 platform/video/serialization/prepare_probe.py
/Applications/Unity/Unity.app/Contents/MacOS/Unity -batchmode -nographics -disable-assembly-updater -projectPath "$project" -executeMethod ValidateRecoveredMedia.Validate -logFile "$PWD/build/video/serialization/unity-validation.log"
cat build/video/serialization/unity-validation.json
