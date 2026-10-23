# Original OpenVR usage and gameplay priority

`openvr-call-sites.json` is a Cecil scan of the retargeted game assemblies,
including nested iterator types, joined to the current native patch manifest.
It contains 102 referenced CVR signatures: 32 native adapters at 49 call sites,
55 explicitly unsupported signatures at 74 call sites, and 15 interface
constructors bypassed by the native managed interface objects. Those counts
measure static references, not observed execution.

`reachability.py` additionally scans every recovered `.unity`, `.prefab`, and
`.asset` using the exact original MonoScript GUIDs. Its reproducible output is
`serialized-openvr-usage.json`. All 46 scenes contain two enabled `FVRViveHand`
components (92 total), and every hand has a non-null serialized Index controller
model reference. There are **zero** serialized `SteamVR_RenderModel`,
`SteamVR_Overlay`, `SteamVR_Skybox`, `SteamVR_Menu`, `SteamVR_Ears`,
`SteamVR_LoadLevel`, or `SteamVR_Behaviour_Skeleton` components. Dynamic component
creation still matters, as described below.

| Remaining APIs | Count | Actual game route and priority |
|---|---:|---|
| CVRInput bone data, origin names, binding UI | 8 | Five bone APIs remain unsupported (count, hierarchy, names, transforms and reference transforms); three APIs are optional action-origin/binding UI helpers. The original game uses five estimated curl summaries and the actual controller root pose, which are now active. Original Index grab, proximity hold, open-hand release and stale-input release passed in the ARM64 player. Full joint tracking and physical calibration remain unproved. |
| CVRScreenshots | 4 | `HookScreenshot` executes at normal SteamVR_Render startup and returns an explicit failure with a once-only warning. The other three APIs require a screenshot request event, which this runtime does not produce. Stereo panorama capture/submission is unavailable. |
| CVRSettings | 2 | `SteamVR_Camera.Expand` can dynamically create `SteamVR_Ears`. It asks whether SteamVR uses external speakers; the absent settings service returns a read error and false, retaining normal head-relative audio. No fabricated persisted setting is reported. Speaker yaw override is unavailable. |
| CVRRenderModels | 9 | Generic SteamVR model loader only; no such serialized components and no direct FistVR callers. Actual game hands already reference their original Index geometry. Lower priority than finger interaction. Dynamic generic SDK uses would still need real mesh/texture/component implementations. D3D11 texture loading is inapplicable on Metal. |
| CVROverlay | 19 | Generic overlay component and texture/progress branches of SteamVR_LoadLevel. All 20 FistVR transition call sites invoke `SteamVR_LoadLevel.Begin`, which creates a fresh loader with null loading/progress/skybox textures. These APIs are therefore not needed by that ordinary route. Optional custom overlays remain unsupported. |
| CVRCompositor skybox | 2 | Only SteamVR_Skybox; zero serialized components and no FistVR calls. Optional six-face skybox override remains unsupported. Real scene-load fades and floor grid are implemented separately. |
| CVRApplications | 3 | Two editor-identification APIs; one optional external-process branch of SteamVR_LoadLevel. Fresh Begin loaders have no internal-process path. No normal scene transition launches a process. |
| CVRTrackedCamera | 7 | Generic SDK camera wrapper only, with no direct FistVR callers or serialized tracked-camera component. Physical passthrough camera acquisition is unavailable. |
| CVRSystem seated recenter | 1 | Only SteamVR_Menu.OnGUI, with no serialized component or FistVR calls. Native play uses calibrated standing/STAGE space; seated-origin recenter is not implemented. |

The original ordinary scene-load blockers—FadeToColor, FadeGrid and
SuspendRendering—now perform real timed Metal blends, calibrated floor-grid
rendering and camera suspension while compositor frames continue. Actual Unity
camera unload/rebind, continuing frame counts and black/grid/restored pixels
passed in the synthetic ARM64 player. The full rebuilt game transition and
firearm interaction still require the staged gameplay acceptance harness.

A second actual control bug was SteamVR_Behaviour.OnCameraPreCull requiring
Camera.stereoEnabled. The native eyes have no Unity XR provider, so the guard
skipped original pose updates. The native patch calls the original once-per-frame
PreCull method. The ARM64 player now proves original SteamVR pose, trigger and
grip actions, one down/up edge each and stale-input expiry.

`FVRViveHand.Update` calls its original vibration action with start=0,
duration=Time.fixedDeltaTime, frequency and amplitude. Those calls reach the real
OpenXR haptic adapter. Disconnected/stale devices return an error. Dispatch is
implemented; physical tactile output is not proved by synthetic testing.

The broker exposes four measured finger-proximity channels (libsurvive axes
4–7), grip force (8), trigger travel (1) and button contacts. The native adapter
now uses those inputs to estimate five curl summaries, explicitly reports
`VRSkeletalTracking_Estimated`, and selects the original SteamVR summary-only
update path. It does not fabricate bone transforms or claim measured splay.
The original `FVRViveHand.PollInput` fixture proves force-started grab, distinct
finger curls, hold after force/contact release, open-hand release, trigger-started
grab and stale-input release. Curls alone do not invent a new grab press. The
optional `FVRMeatHands` animation consumes the same curl values, but its visual
result has not been verified. See [ESTIMATED-SKELETON.md](ESTIMATED-SKELETON.md)
for input mapping, test boundaries and remaining calibration limitations.

Primary raw input/button mapping is implemented, but Valve's proprietary force
button/touch thresholds and hysteresis have not been reproduced or physically
calibrated. The bridge's current force-grip press threshold is 0.5. This remains
a control-feel limitation even when the diagnostic grip acceptance test passes.

No new success stubs were added merely to reduce the unsupported count. The
remaining methods return their explicit service errors or throw when the API
has no error result. Native camera/pose/trigger/grip/haptics, chaperone bounds,
projection, display frequency and ordinary scene fading have concrete adapters.

## Current composition review and practical priority

`openvr-call-sites-current.json` repeats the scan against the current
`build/composed/final/vr-patch-manifest.json`: the counts remain 32 adapted,
55 unsupported and 15 bypassed constructors. This review does not turn static
reachability into runtime proof. The prior actual main-menu log
`build/native-player.log` specifically confirms two unsupported calls executed:
`CVRScreenshots.HookScreenshot` and `CVRSettings.GetBool`. Both produce the
adapter's explicit feature-unavailable warning. The latter is the
`steamvr/usingSpeakers` lookup in `SteamVR_Ears.OnEnable`; it returns a read error,
so the original listener retains head-relative orientation. Returning settings
success merely to remove this warning would falsely imply a SteamVR settings
service exists.

Priority for the remaining work:

1. **Base gameplay acceptance:** run the actual main menu → indoor range →
   asynchronous M1911 load → collision grip → proximity hold → original
   projectile flight → release harness. No remaining unsupported CVR signature
   has been demonstrated to block that route. Runtime exceptions and captures,
   rather than the static supported-method count, decide acceptance.
2. **Physical calibration and control validation:** the original
   `FVRMovementRig.Update → FVRPlayArea.TryGetPlayArea` path uses the implemented
   calibration state and bounds rectangle. The pinned runtime obtains live
   width/depth from validated room calibration and leaves absent bounds unknown;
   its 2×2 m rectangle is restricted to explicit simulation. This port does not
   supply the runtime's room-setup UI, and synthetic results do not validate a
   physical floor, boundary visibility, force thresholds, haptics or tracked
   fingers. Those remain higher-value practical checks than optional SDK APIs.
3. **Seated origin and controller binding UI:** seated recenter is reachable
   from the generic SDK `SteamVR_Menu.OnGUI` only. That menu has zero serialized
   instances and no dynamic creation call was found. Native bootstrap explicitly
   selects standing space. No original FistVR call to origin-name/binding-hint UI
   helpers was found; its own controller mode, action polling and models follow
   the existing native bridge. Seated-origin support would require consistent
   transformations across head, both eyes and controllers; a recenter no-op
   would be incorrect. Binding overlays would require a real configuration UI.
4. **Optional SDK services:** SteamVR screenshots, custom overlays/skyboxes,
   passthrough camera capture, external-process launching and generic runtime
   render-model downloading remain unavailable. They are not replaced with
   successful no-ops. Original game scene-load methods construct fresh loaders
   with null overlay/skybox textures and no external-process target.

The expanded serialized scan also finds zero `ControllerButtonHints` and zero
`SteamVR_TrackingReferenceManager` components. These are the two recovered SDK
classes that dynamically add `SteamVR_RenderModel`; finding no serialized
render-model component alone would have been an incomplete argument. Dynamic
creation from unknown mods or external code is outside this original-content
audit.

No managed/native composition inputs were changed for this review. The
remaining optional calls do not justify fabricating settings, bone poses,
overlay handles or successful screenshot registration.

Regenerate the method scan:

```sh
dotnet run --project audit-vr/usage/Usage.csproj -- audit-managed/ported \
  native/build/ported-vr-estimated/vr-patch-manifest.json \
  audit-vr/openvr-call-sites.json
python3 audit-vr/reachability.py
```
