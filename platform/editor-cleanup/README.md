# Recovered post-processing editor cleanup compatibility

The recovered player assembly's
`UnityEngine.Rendering.PostProcessing.RuntimeUtilities.Destroy(Object)` contains
only the Unity-null guard followed by `Object.Destroy(obj)`. The original
compile-time editor branch is absent. During scene replacement in the editor,
`PostProcessLayer.OnDisable -> PostProcessBundle.Release -> RuntimeUtilities.Destroy`
therefore calls delayed destruction in edit mode and Unity logs:
`Destroy may not be called from edit mode! Use DestroyImmediate instead.`

The patch inserts exactly seven IL instructions before that original call:

```csharp
// Existing Unity null/fake-null guard remains unchanged.
if (Application.isEditor && !Application.isPlaying)
    Object.DestroyImmediate(obj, false); // allowDestroyingAssets remains false
else
    Object.Destroy(obj);                 // exact original instruction
```

No runtime helper, serialized type, UnityEditor dependency or assembly reference
is added. Null/fake-null handling, player behavior and editor play-mode behavior
remain intact. The scope is only this post-processing cleanup utility; other
Destroy calls are unchanged. Immediate destruction is restricted to non-playing
editor mode and does not permit destruction of assets.

From the workspace root, compose against the current Game output, never replace
other agents' changes with an older baseline:

```sh
dotnet run --project platform/editor-cleanup/patch/Patch.csproj -c Release -- \
  <current-Game.dll> <next-Game.dll>
```

`UNITY_CONTENTS` selects engine references; otherwise the workspace's native
Unity 2021 installation is preferred. The patch refuses already-patched or
unexpected method shapes. It can run after the existing managed composition;
it introduces no self AssemblyRef. A prepared example is
`build/editor-cleanup/NativeH3VR.Game.dll`, with a sidecar change report.
It has not been staged into either working Unity project.

Validation:

```sh
bash platform/editor-cleanup/test.sh
```

The actual Cecil patch is applied to a fixture containing the original method.
Twelve baseline and twelve patched cases cover both player playing states, both
editor playing states, live objects, null objects and Unity-style destroyed
objects. Player states and editor play mode call only deferred Destroy; only
non-playing editor mode calls DestroyImmediate with `allowDestroyingAssets=false`.
Null/destroyed objects call neither API nor environment getters. Patching the
actual latest composed Game DLL also succeeded. This is semantic branch proof,
not a claim of a completed live editor rerun.

The earlier `build/native-build.log` contains this exact call stack during
`BuildPipeline.BuildPlayerInternal` and subsequently reports
`NATIVEH3VR_BUILD_RESULT Succeeded`. Thus these messages did not by themselves
fail that development build. They still fail a clean-error-log acceptance rule
and should not be ignored in strict preflight. Whether Unity records a given
message as a BuildReport error depends on where the callback runs; no claim is
made that the old successful build proves `BuildOptions.StrictMode` would pass.
The required next check is scene open/close and player-build preflight after
root stages this patch at its controlled editor checkpoint.
