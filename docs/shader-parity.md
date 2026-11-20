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

## Optic blit passes

The original FX manager serializes the scope postprocess shader directly and its runtime executes eight named passes by index. The converter removes the recovered shader's light-baking `Meta` tags so Unity 6.7 keeps those passes in players. It also keeps variants selected by the runtime overlay keyword. [Unity Meta pass documentation](https://docs.unity3d.com/6000.0/Manual/MetaPass.html)

`OpticBlitWitness.cs` runs the same deterministic source and overlay textures through all eight passes with the overlay keyword on and off. Compile it against retail with the Roslyn references above plus `UnityEngine.VR.dll`. Bootstrap `NativeH3VR.QA.OpticBlitWitness` and set `NATIVEH3VR_OPTIC_BLIT_WITNESS` to a private Windows output directory. For a Unity 6.7 test fixture, add the authored witness to a scene and put the privately migrated shader at `Assets/Resources/Optics.shader`. Use the same environment variable with the local player output path. The witness records pipeline, graphics API, color space, indexed pass names, frame time and linear RGBA readbacks.

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

## Raised Take & Hold cover

The stock steel barriers use a fully metallic Alloy material. Their room reflections provide most of the visible surface detail. The manual stereo renderer on macOS tests the stock probe bounds against the two submitted eye frusta. On Unity-managed cameras, the game uses Unity's culling group. The importer restores the Steam baked probe textures and scene lighting during a full player conversion.

To inspect the first Hold, use `--profile --tnh Classic --tnh-seed 0` with `--builtin` or `--preset balanced` or `--preset quality`. Add `--inspect-cover` and supply the `--app`, `--runtime`, `--launcher` and `--out` paths from the previous example. To compare builds at the same pose, pass the first run's Hold state to `--tnh-observer-from`. The witness records the live view, freezes the game, temporarily removes smoke and captures both eyes with the actual material, a coverage mask and lighting controls.

```sh
uv run tests/ShaderParity/cover_pixels.py "$HOLD_CAPTURE" --verify --out "$COVER_JSON"
```

The pixel test checks the masked steel surface in each eye, checks the albedo control, compares normal rendering with the same room's probes forced on, and records the number of raised meshes that selected a room reflection.
