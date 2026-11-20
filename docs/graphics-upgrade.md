# Optional URP renderer

`--graphics-upgrade` builds the game and its stock bundles with Unity 6.7 and the editor’s bundled Universal Render Pipeline 17.7.0. The converter installs the rendering code into the generated project and migrates materials from the local Steam import. The option is also available in the terminal wizard. To return to the stock rendering path, launch the player from your original workspace.

```sh
dotnet run --project src/H3VR.Converter -c Release -- --graphics-upgrade
```

The default graphics-upgrade workspace is `.local/workspace-graphics-upgrade`. Supply `--workspace` to use another private directory. If you already have a verified extraction from the same converter inputs, add `--reuse-extraction /path/to/workspace`. Add `--compact-player-data` to pack the player data with chunked LZ4. On macOS, launch the generated app through the MacVR launcher. [macOS launch instructions](platform-integrations.md)

## Graphics Options

Open **Graphics Options** from the game’s options panel. **Balanced** is the initial preset. Changing a value applies it immediately, updates the displayed preset and saves your choices across launches. **Game Settings** opens the stock controls for game quality, texture filtering, particle collisions and scope MSAA.

| Setting | Performance | Balanced | Quality |
| --- | --- | --- | --- |
| Render scale | 85% | 100% | 100% |
| MSAA | Off | 2× | 4× |
| Shadow distance | 15 m | 30 m | 60 m |
| Main shadow atlas / cascades | 512 / 1 | 1024 / 2 | 2048 / 4 |
| Additional lights per object | 2 | 4 | 8 |
| HDR rendering | Off | On | On |
| Bloom | Off | Subtle | Full |
| Sharpening | Off | Low | Low |
| Tone mapping | Off | Neutral | ACES |
| Ambient occlusion | Off | Off | On |

Render scale ranges from 70% to 130% of the output dimensions. On MacVR the compositor continues to receive the headset’s requested eye size and projection. HDR controls the scene color buffer. Bloom, tone mapping and ambient occlusion can each be disabled independently.

## Rendering

The renderer uses URP Forward with SRP batching. The material conversion preserves the stock asset identities used by scenes, prefabs and stock bundle addresses. Common Alloy surfaces use an authored URP PBR shader for their original packed metal, occlusion, specularity and roughness channels. Specialized effects retain recovered shader programs. The generated project records material-family counts and changed assets in `ProjectSettings/NativeH3VRGraphicsMaterials.json`.

The optional bloom pass extracts highlights at quarter width and height. One full-resolution pass applies the selected bloom, bounded sharpening and tone curve. With all three controls off, the renderer skips both passes and their intermediate textures. The near-field ambient occlusion option reconstructs normals from depth and runs at half resolution. These effects operate on player views and bypass offscreen scope, reflection and preview renders.

macOS renders both tracked views through URP render requests. When HDR or MSAA needs a separate scene texture, the bridge reuses one allocation for consecutive eyes and resolves into two persistent compositor textures. Windows uses Unity’s OpenXR rendering path.

Stock scope and spectator cameras keep their per-camera culling and cleanup callbacks under URP, including nested renders. The game’s procedural trails and temporary view feedback have their own render passes. The renderer skips those passes when no effect is active.

The stock game warms the same shader collection during engine startup and when its game manager awakens. The URP build filters legacy vertex-lit, forward and lightmapping-only pass entries from that warmup. On the tested Steam import this reduces it from 842 serialized entries to 155. A complete copy of the collection retains the original variants for build inclusion.

Both rendering paths reuse a pair of eye matrices after Unity copies them and defer allocation of the camera-relative material property block until a renderer writes to it. In a Unity 6.7 editor benchmark of the imported stock callback, the median with one initialized renderer fell from 1.54 to 0.54 µs. Measured heap growth fell from about 159 bytes per call to zero after warm-up.

## Testing an imported macOS build

The test runner uses a separate preferences directory and a diagnostic mod bound to the selected build. Set `APP`, `LAUNCHER` and `MACVR` to the generated app, UnityMetalCli DLL and local MacVR runtime export. Choose fresh output directories.

```sh
python3 tests/GraphicsUpgrade/run.py --app "$APP" --launcher "$LAUNCHER" \
  --runtime "$MACVR" --out "$PWD/.local/graphics-ui-check"
python3 tests/GraphicsUpgrade/run.py --profile --scene WarehouseRange_Rebuilt \
  --app "$APP" --launcher "$LAUNCHER" --runtime "$MACVR" --out "$PWD/.local/graphics-profile"
uv run tests/GraphicsUpgrade/measure.py "$PWD/.local/graphics-profile"
```

The interaction check exercises the stock panel, controller ray, preset and preference restore. The profile run captures each submitted eye, switches presets at a fixed pose and records renderer settings, stereo CPU submission and compositor frame intervals. The Metal GPU counter times the Unity command buffer that owns the OpenXR submission event. Earlier command buffers can contribute additional GPU work.
