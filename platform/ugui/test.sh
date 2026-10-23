#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
root="$PWD"
project="$root/platform/ugui/probe"
mkdir -p "$project/Assets/Plugins"
cp audit-managed/ported/UnityEngine.UI.dll build/composed/final/NativeH3VR.Compat.dll "$project/Assets/Plugins/"
cp unity/Assets/Plugins/UnityEngine.UI.dll.meta "$project/Assets/Plugins/"
/Applications/Unity/Unity.app/Contents/MacOS/Unity -batchmode -nographics -disable-assembly-updater -projectPath "$project" -executeMethod BuildUIProbe.Prepare -logFile "$root/build/ugui/prepare.log"
cp build/ugui/runtime/UnityEngine.UI.dll "$project/Assets/Plugins/"
/Applications/Unity/Unity.app/Contents/MacOS/Unity -batchmode -nographics -disable-assembly-updater -projectPath "$project" -buildTarget OSXUniversal -executeMethod BuildUIProbe.Build -logFile "$root/build/ugui/probe-build.log"
diff build/ugui/old-script-ids.txt build/ugui/new-script-ids.txt
codesign --force --deep --sign - build/ugui/UIProbe.app
NATIVEH3VR_UI_RESULT="$root/build/ugui" 'build/ugui/UIProbe.app/Contents/MacOS/Native H3VR UI Probe' -force-metal -logFile "$root/build/ugui/player.log"
cat build/ugui/result.json
