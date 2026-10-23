# Bounded original-game acceptance route

`NativeGameplayProbe.cs` is an opt-in component for the full game build. The native full-content diagnostic player completed the original M1911 and AKM routes; their hash-bound logs and captures are under `build/player-attempts/diagnostic-grip-root` and `diagnostic-akm`. Those runs use the earlier rendering payload and a diagnostic video adapter. They establish those interaction routes, not acceptance of the later combined build or complete gameplay parity.

Build with `python3 platform/gameplay-probe/build.py --managed <latest-composed-managed-directory>`. Stage the resulting DLL in the full project's Plugins before building, so Unity registers its RuntimeInitializeOnLoadMethod. No modification of the root diagnostics component is required.

Launch a headless/private-Home synthetic game with:

```
NATIVEH3VR_OPENXR=synthetic
NATIVEH3VR_DIAGNOSTIC_INPUT=1
NATIVEH3VR_GAMEPLAY_PROBE=1
NATIVEH3VR_TEST_DURATION=0
```

Optional environment: `NATIVEH3VR_GAMEPLAY_SCREENSHOT=<absolute png>`, `NATIVEH3VR_GAMEPLAY_SCENE=IndoorRange_Updated`, `NATIVEH3VR_GAMEPLAY_ITEM=M1911`. The main diagnostics timed quit must be disabled (`TEST_DURATION=0`) so it cannot preempt this probe's own 180-second limit. The harness compiles against the workspace Unity 2021 engine by default when installed; `--unity-contents` can select another engine. The player logs `NATIVE_GAMEPLAY_STAGE` throughout, exits0 only after the complete route, and exits1 with the failing stage on exception or timeout.

The default route waits for MainMenu3 and the original ammunition prewarm, then calls original SteamVR_LoadLevel.Begin("IndoorRange_Updated"). That exercises the original fade/load/pause/resume sequence. It resolves IM.OD["M1911"] (original prefab GUID7ec8bdc68828d214cbdade8ebbf4791c), calls the original FVRObject.GetGameObjectAsync/Anvil callback, instantiates the genuine firearm, checks missing scripts, and records loaded asset bundles. It verifies the original persistent stereo renderer survives the scene transition, its source camera belongs to the range scene, completed GPU frames continue and actual camera pre/post callbacks execute. It waits for original Index controller initialization before positioning the real prefab at a stationary simulated hand, holds its Rigidbody still while arranging the test, waits for actual collision hover, then supplies a grip press through the native action bridge. It does not call RetrieveObject, ForceSetInteractable or BeginInteraction. It verifies the game's own hand/weapon ownership, releases squeeze force while retaining distinct measured-channel diagnostic proximity values, and requires the original estimated curl path to keep the gun held. It supplies a trigger press through original SteamVR actions and FVRViveHand polling, then opens the simulated hand and requires the game to release weapon ownership.

`NATIVEH3VR_GAMEPLAY_FIRE=1` additionally prepares one live round through `NativeGameplayLoadFixture`, using original load/save-state and safety APIs, then pulls the simulated trigger. The fixture checks that the chosen ammunition exists in the original ammunition registry and that the firearm's loaded round matches it. Exactly one original FVRSceneSettings.ShotFiredEvent must be observed, after which the trigger is released while the grip remains held. That event alone is insufficient: the original code emits it before creating bullets and silently skips a missing ballistic prefab. The harness therefore first validates the loaded round's projectile count and real BallisticProjectile prefab, records pre-existing projectile instance IDs, then requires the expected number of new instances of that exact prefab. Read-only inspection of original private trajectory fields must show a start point within 10 cm of this shot's muzzle, finite positive initial firing velocity, and more than 1 mm of actual path travel. No projectile is fabricated, no original Fire method is called by the test, and projectile fields are never written. This route follows normal game statistics behavior. Without this flag it proves unloaded trigger input delivery, not firearm discharge.

The pure acceptance predicate is shared with `BallisticEvidenceFixture.cs`; its 17 checks reject unmatched instances, another muzzle, never-fired objects, zero travel, negative values and non-finite values. This fixture and the full harness compilation passed against the staged Game/FirstPass and actual Unity 2021 engine references. The pure checks alone are not an actual full-game firing result. Each player run must also pass the runner's runtime-error gate; a shot event emitted before a later Unity exception cannot establish a clean successful run.

Synthetic input is process-local, explicitly enabled separately from synthetic head tracking, and unavailable in live mode. Controller commands expire after one second without refresh. This tests software integration; it does not establish physical tracking, ergonomics, haptics, throwing, full finger/bone tracking or complete gameplay parity.

The gameplay screenshot is taken from the active range after trigger acceptance,
while the loaded firearm is still held. The harness waits for two more completed
native stereo frames, verifies the source camera belongs to that scene, removes
any old output at the requested capture path and requires a fresh nonempty PNG.
It then tests open-hand release before emitting the final pass marker. The menu's
timed screenshot coroutine is disabled by `NATIVEH3VR_TEST_DURATION=0`.

The runner accepts `--scene <original-build-scene>` and `--item <original-object-id>`
with `--gameplay`; fixtures support handguns, closed-bolt weapons with an original
single-shot selector, double-action revolvers, bolt-action rifles with a closed
bolt, and tube-fed shotguns with a closed bolt. This is one-round fixture support,
not coverage of reloading, cycling, attachments or every weapon of those classes.
Open-bolt weapons are rejected because they require the original magazine-feed
and bolt-cycle path. Only the native trigger can fire the prepared weapon.
`--scene-smoke --scene
<name>` instead follows the original scene transition, requires continued native
stereo and original camera callbacks, observes the scene for five seconds and
takes fresh captures. It does not claim firearm or game-mode acceptance. Use
`--output-dir build/scene-runs/<name>` to preserve separate evidence for each
scene. Both routes fail on logged errors even after their completion marker.
Original missing-script warnings are accepted only during the requested scene's
load, with exact original object names and counts. The allowance requires the
existing structural scene proof to match the actual packaged scene, build
settings and every shipped managed assembly; warnings outside that load or
beyond its original counts still fail. Accepted warnings remain in the report.

The optional scope graphics fixture can API-mount the original EC79A2 to the
original C7A2. Add these variables to the synthetic scope-probe environment:

```sh
NEWENGINEH3VR_SCOPE_PROBE=1
NEWENGINEH3VR_SCOPE_ITEM=EC79A2
NEWENGINEH3VR_SCOPE_MOUNT_ITEM=C7A2
NEWENGINEH3VR_SCOPE_OUTPUT=/absolute/path/to/separate/optics-report
NEWENGINEH3VR_SCOPE_EYE=left
NEWENGINEH3VR_SCOPE_HIDE_OWN_MESHES=0
```

Only this exact item pair and its original prefab GUIDs are admitted. The fixture
loads both through the original Anvil API, validates the unique `_RailMount_Top`
Picatinny mount, applies the original permitted scale and `AttachToMount` APIs,
and starts at the rail midpoint before the game's normal rail clamp. It freezes
only the owned gun/optic and aligns their complete attached assembly to the
selected eye. Original attachment callbacks determine controller settings;
the fixture does not set optical parameters or force scope rendering.

The additive `mountedFixture` report records mount registrations, hierarchy,
placement/scale, controller indices, unheld state, and original detach/cleanup.
This is an **API-mounted synthetic graphics fixture**, not hand attachment,
physical handling or headset acceptance. Unknown or ambiguous mappings fail.
The own-mesh experiment remains opt-in and hides only the optic's owned meshes
during its scope pass; it does not hide the mounted gun. Keep it off when testing
the complete original assembly and shader fix.
