# Shader parity checks

The inventory reads local player data and writes private JSON records of serialized shader identities, pass tags, stage counts, compute kernels, material assignments and saved properties. It accepts loose player data, UnityFS player archives and Unity 6.7 v9 streaming bundles. Shader identity comparisons use serialized names and list all candidates when a name appears in multiple collections. Material assignment comparisons use a material's name and serialized collection or StreamingAssets bundle path. The report lists split or moved material groups so those can be inspected separately.

```sh
python3 -m venv .local/shader-audit-venv
.local/shader-audit-venv/bin/pip install UnityPy==1.25.4
.local/shader-audit-venv/bin/python tests/ShaderParity/inventory.py \
  --stock "$stock/h3vr_Data" \
  --builtin "$builtin_app/Contents/Resources/Data" \
  --urp "$urp_app/Contents/Resources/Data" \
  --out .local/shader-parity/inventory
```

Check each inventory's `errors` before comparing counts. An unresolved `unity default resources` reference can point to data provided by the engine executable. A candidate's presence in the bundle establishes serialized coverage. GPU parity requires captures with the pass's actual inputs, including mesh attributes, pipeline lighting, probes, blend state and auxiliary textures.

To check the baked lighting shipped in a player, run the following against the same two Data directories. The check follows every scene's color, direction and shadowmask slots, hashes the bound GPU texture blocks and compares dimensions, format, mip count, color space and lightmap encoding. It reads the compact player archive in place.

```sh
.local/shader-audit-venv/bin/python tests/ShaderParity/compare_baked_lighting.py \
  --stock "$stock/h3vr_Data" --player "$builtin_app/Contents/Resources/Data" \
  --out .local/shader-parity/steam-vs-builtin-lightmaps.json
```

In the Unity 6.7 Built-in build of Steam build 25002541, all 72 bound textures across 46 scenes matched their retail payloads and metadata. They comprised 65 color lightmaps, four directional lightmaps and three shadowmasks.

The floating Take & Hold lettering uses authored mipmaps. Regenerating them from the largest image brightened the red lettering at a distance. Two premultiplied warning panels in Room 4 Plinths also depend on their original RGB values in transparent texels. The converter reads these four BC3 textures from the local Steam game and binds the full original mip chains to the corresponding materials. A fifth texture supplies the packed material mask for SilverBulletCrater. The following check follows the named material slots in the cooked player and compares every byte, mip count, color space and sampler setting.

```sh
.local/shader-audit-venv/bin/python tests/ShaderParity/compare_material_mips.py \
  --stock "$stock/h3vr_Data" --player "$builtin_app/Contents/Resources/Data" \
  --binding sharedassets18.assets/TAH_TouchToBegin/_MainTex \
  --binding sharedassets18.assets/TAH_HoldPoint/_MainTex \
  --binding sharedassets12.assets/MG_4Plinth_Warning1/_MainTex \
  --binding sharedassets12.assets/MG_4Plinth_Warning2/_MainTex \
  --binding sharedassets16.assets/SilverBulletCrater/_SpecTex \
  --out .local/shader-parity/steam-vs-builtin-material-mips.json
```

## Retail first Hold reference

Use a private full copy of the Steam game on a system where the retail Windows player can render. The witness uses the stock sequence and equipment settings, enters the Classic Hallways lobby, starts the first Hold and turns the headset toward the nearest active steel cover. It records the original mesh renderers, material assignments, scene reflection probes and per-cover probe selection. The witness writes `take.json` and `hold.json` into an isolated output directory. The retail game continues rendering for eight seconds after writing `hold.json` so a stereo capture can be taken. The gameplay state uses a custom health option to keep score submission disabled.

Use the SDK's Roslyn compiler with the original player's managed references. In this shell example, `stock` points to the read-only Steam game directory, `clone` is a separate game copy, `scratch` is a fresh private output directory and `csc` is the SDK's `Roslyn/bincore/csc.dll`.

```sh
managed="$stock/h3vr_Data/Managed"
witness="$scratch/NativeH3VR.QA.RetailHoldWitness.dll"
patched="$scratch/Assembly-CSharp.dll"
dotnet "$csc" /nologo /noconfig /nostdlib /target:library \
  /r:"$managed/mscorlib.dll" /r:"$managed/System.dll" /r:"$managed/System.Core.dll" \
  /r:"$managed/UnityEngine.dll" /r:"$managed/UnityEngine.UI.dll" \
  /r:"$managed/Assembly-CSharp.dll" /r:"$managed/Assembly-CSharp-firstpass.dll" \
  /r:"$managed/Valve.Newtonsoft.Json.dll" /out:"$witness" \
  tests/ShaderParity/RetailHoldWitness.cs
dotnet run --project tests/ShaderParity/StockBootstrap -- \
  "$managed" "$witness" NativeH3VR.QA.RetailHoldWitness "$patched"
cp "$patched" "$witness" "$clone/h3vr_Data/Managed/"
```

Launch the copy with `NATIVEH3VR_RETAIL_HOLD` set to an absolute **Windows** path for the witness output. Under Wine this can be a `Z:\…` path pointing into the private scratch directory. Use a dedicated Wine prefix and runtime state, or a separate Windows test account, to isolate the game's profile. The macOS reference used Wine/DXMT for D3D11, OpenComposite, and a headless MacVR test runtime with fixed synthetic input and compositor capture. Inspect `hold.json` to confirm scene `TakeAndHoldClassic`, state `Hold`, sequence seed `0`, and active high and low meshes on the original Alloy/Core material. Keep the patched managed assembly, local shader inventories and captures in the private scratch directory.

To compare the three benchmark maps, set `NATIVEH3VR_RETAIL_HOLD_OBSERVER` to a native profiler's first-Hold state JSON. The retail game checks the stock route and first objective, captures the opening Take from the original spawn and replays the recorded Hold observer during an active hacking wave. A capture driver can set `NATIVEH3VR_RETAIL_HOLD_CAPTURE_ACK=1` and write `take.captured` and `hold.captured` into the witness output after saving each stereo image.

## Optic blit passes

The original FX manager serializes the scope postprocess shader directly and its runtime executes eight named passes by index. The converter removes the recovered shader's light-baking `Meta` tags so Unity 6.7 keeps those passes in players. It also keeps variants selected by the runtime overlay keyword. [Unity Meta pass documentation](https://docs.unity3d.com/6000.0/Manual/MetaPass.html)

`OpticBlitWitness.cs` runs the same deterministic source and overlay textures through all eight passes with the overlay keyword on and off. Compile it against retail with the Roslyn references above plus `UnityEngine.VR.dll`. Bootstrap `NativeH3VR.QA.OpticBlitWitness` and set `NATIVEH3VR_OPTIC_BLIT_WITNESS` to a private Windows output directory. For a Unity 6.7 test fixture, add the authored witness to a scene and put the privately migrated shader at `Assets/Resources/Optics.shader`. Use the same environment variable with the local player output path. The witness records pipeline, graphics API, color space, indexed pass names, frame time and linear RGBA readbacks.

In a full converted game, run `tests/GraphicsUpgrade/run.py --optics` with the usual `--app`, `--runtime`, `--launcher` and `--out` arguments. This resolves the shader from the game's active scope postprocess material. Check the recorded shader and night-vision parameters in `inputs.txt` when pairing the 16 images with their retail references. The signal-to-noise setting changes the rendered grain.

`RetailThermalWitness.cs` loads the stock handheld thermal camera from the game item database. It captures the ordinary view, raw sensor, processed lens texture and ordinary reset using the camera's own targets. Compile it with the retail references above, bootstrap `NativeH3VR.QA.RetailThermalWitness` and set `NATIVEH3VR_THERMAL_WITNESS` to a private output directory. For a converted player, run `tests/GraphicsUpgrade/run.py --thermal` with the player, runtime, launcher and output paths. The witness exits after writing `thermal.json`.

## Whole-game material sweep

Build the retail QA copy using the same Roslyn references described above and compile `MaterialSwatches.cs` together with `BundleSwatches.cs`. Bootstrap `NativeH3VR.QA.MaterialSwatches`. Set `NATIVEH3VR_SHADER_SWATCHES` to the private output path, `NATIVEH3VR_SWATCH_SCENES=*` and `NATIVEH3VR_SWATCH_BUNDLES=1`. This loads the game's build scenes, resources and materials reached from the stock bundle addresses. The witness renders the materials to an isolated GPU chart using a fixed mesh, lights and reflection environment. The per-scope JSON records renderer, material and texture bindings. Some shaders need a specific particle stream, skinning, camera texture, render target or postprocess input. Their behavior also needs a scene or pass-specific check.

Run the same chart in each converted player. Use a fresh output directory for each run.

```sh
python3 tests/GraphicsUpgrade/run.py --swatches --scene '*' --swatch-bundles \
  --app "$APP" --runtime "$MACVR" --launcher "$LAUNCHER" --out "$PLAYER_SWATCHES"
uv run tests/ShaderParity/compare_swatches.py --stock "$RETAIL_SWATCHES" \
  --player "$PLAYER_SWATCHES" --scene '*' --out "$COMPARISON_JSON"
```

The comparator checks visible material coverage, near-black pixels and error-shader magenta. It pairs materials by their texture recipe or a one-to-one renderer and material-slot path. The report identifies generated defaults, numbered-texture candidates, conflicting slots and unmatched rows. A numbered texture name alone cannot establish texture identity. A dark surface in both players is recorded as authored or shared darkness.

### Texture channels

Some opaque materials store roughness or masks in texture alpha. Unity's transparent-colour dilation can then overwrite visible colour in texels whose alpha happens to be zero. On the Friendly 45 pine, the original material's median RGB was (15, 14, 11). Reimport with dilation produced (95, 100, 56). Preserving the original colour gave (14, 15, 11). The converter identifies the verified material and texture usages before configuring the importer.

In the recovered DXT5 normal PNGs, red and green contain the tangent coordinates. The PNG alpha comes from the red channel of the packed Steam texture. Treating it as coverage changed both tangent coordinates in the editor import. On a 1,024-sample Metal grid of the Dumple normal map, mean X/Y errors fell from 24.06/23.91 to 0.12/0.04 on a 0–255 scale. On the block normal map they fell from 12.22/13.30 to 0.46/0.43. Both checks read the original compressed game texture and the actual Unity 6.7 GPU import.

The original Farout rim effect samples an uncompressed RGB24 noise texture. Block compression changed its sampled colour by a mean of 28.36 levels in linear rendering. Restoring uncompressed import reduced the error to 0.015 levels across all 4,096 texels. The same default-platform rule preserves original uncompressed RGB24, RGBA32 and ARGB32 colour textures. Platform-specific import overrides retain their settings.

### Two-sided materials

The original Direct3D programs distinguish the front and back of leaves, paper targets and other two-sided materials. Some recovered fragment programs tested the signed `VFACE` value as a boolean. That made both windings take the front-facing branch. During conversion these programs now use the boolean `SV_IsFrontFace` input. Where the original instruction combines the face mask with another flag, the converter reconstructs its full 32 bits. The migration applies on all graphics APIs.

On a fixed quad using Friendly 45's original birch frond material and textures, the Steam player produced median front and back RGB values of (90, 87, 82) and (36, 34, 32). With the recovered shader, both sides measured (99, 95, 92) on Metal. The corrected back measured (35, 33, 32). The correction preserved every front-facing pixel and the cutout coverage in the Metal fixture. The matching leaf test changed all 1,192 covered back-facing pixels. A separate float-target test checked both windings and the exact bit pattern on Windows D3D11 and macOS Metal.

### Shared sky projection

The toolbox prefab and two Grillhouse projection volumes reference the same outdoor sky material. Unity invokes the prefab's edit-mode callbacks during a batch import. In a full conversion these callbacks overwrote the sky's box extents of approximately (105951, 77375, 104094) with the toolbox preview's (2, 2, 2). The managed migration guards the edit-time material writes during a batch import. A Unity 6.7 test exercised both original callbacks using the Steam material. It reproduced the overwrite in the batch editor, retained the outdoor projection after migration and verified that both callbacks still updated the projection during play.

## Raised Take & Hold cover

The stock steel barriers use a fully metallic Alloy material. Their room reflections provide most of the visible surface detail. The manual stereo renderer on macOS tests the stock probe bounds against the two submitted eye frusta. On Unity-managed cameras, the game uses Unity's culling group. The importer restores the Steam baked probe textures and scene lighting during a full player conversion.

To inspect the first Hold, use `--profile --tnh Classic --tnh-seed 0` with `--builtin` or `--preset balanced` or `--preset quality`. Add `--inspect-cover` and supply the `--app`, `--runtime`, `--launcher` and `--out` paths from the previous example. To compare builds at the same pose, pass the first run's Hold state to `--tnh-observer-from`. The witness records the live view, freezes the game, temporarily removes smoke and captures both eyes with the actual material, a coverage mask and lighting controls.

```sh
uv run tests/ShaderParity/cover_pixels.py "$HOLD_CAPTURE" --verify --out "$COVER_JSON"
```

The pixel test checks the masked steel surface in each eye, checks the albedo control, compares normal rendering with the same room's probes forced on, and records the number of raised meshes that selected a room reflection.

The room's teal painted stripes are a separate test of baked lighting. The actual wall uses a decal on Alloy/Core and a nondirectional lightmap. Unity selects the lightmap keyword per draw. During staging the converter retains the engine-controlled lightmap keyword groups and enables explicit build-time retention of the baked modes. The source shader still selects the original material's decal and mask. An exact wall-tile fixture reproduced the lost stripe with automatic stripping and restored it in a standalone player after retaining the required keyword combination. [Unity shader variant stripping](https://docs.unity3d.com/6000.0/Documentation/Manual/shader-variant-stripping.html)

For the matched retail observer facing the teal wall, add `--retail-room /path/to/steam-stereo.png` to the pixel test. It checks for the visible teal band in the right-hand wall region of each eye and records the cropped sample counts. Other observer positions can face different walls or have the wall entirely occluded.

## Northest Dakota material identification

On stock sequence 1, the opening supply area faces a green container and a distant snowy mountain. Add the following arguments to a Take & Hold profile run to identify the visible materials and isolate their lighting at the same headset pose.

```sh
--tnh NorthestDakota --tnh-seed 1 --preset balanced \
  --inspect-take-material StorangeContainer_10ft_Green_Snowy \
  --inspect-take-material 'FinalMountain_*'
```

The witness freezes game time after the opening Take measurement. It marks each material group, saves both eyes and records the assigned shader, keywords, reflection probes and lightmap index. Further captures isolate grading, realtime shadows and the selected renderers’ baked lightmaps. It restores the original materials and settings before entering the first Hold.

In the earlier Balanced player, marking each group covered its entire reference crop. The container bound Alloy/Oriented/Blend. The distant mountain bound Alloy/Core with a decal. A separate mask on all 996 renderers using the nearby CynCliff material covered zero pixels in the distant mountain crop.

## Game cameras and view fades

The stock handheld thermal camera and T7 use the game's picture-in-picture camera to select a replacement shader. The URP conversion builds its opaque, cutout and transparent programs from the locally recovered Steam shader. A thermal camera draws the applicable replacement with each object's own material properties. The effective `RenderType` of the original material selects the program, including materials that override their shader's tag.

The graphics interaction test also triggers the stock camera fade and reads both submitted eyes. It checks an opaque red stimulus and the return to the menu after a timed fade. The stock player uses the same component for death, teleport and ending transitions. The URP player advances the original transition on game updates and draws the selected color before grading. It skips the draw when the fade is clear. [Unity render pipeline feature comparison](https://docs.unity.com/en-us/engine/6000.0/manual/render-pipelines/choose-a-render-pipeline/feature-comparison)
