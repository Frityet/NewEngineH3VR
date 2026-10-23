# Corrected-emitter probe on Unity 2021 ARM64

This closes the editor-version mismatch recorded by the original Unity 2020 proof.
The isolated color-path probe was rebuilt with the local ARM64 Unity 2021.3.45f2
editor from the saved `native-proof` wrappers and the minimal `color-path-probe`
project skeleton. The source manifest hash-binds all four positive wrappers to the
freshly emitted include bodies, and records that the eight `Broken` and eight
`SwizzleOnly` controls are unchanged.

The 2021 editor build succeeded and emitted an ARM64 player. Running that player on
Metal / Apple M5 Max passed all 32 corrected samples, detected all 32 original-bad-
emission negatives and all 32 swizzle-only negatives, and measured a maximum
positive error of `6.088614e-05` with zero runtime errors. These aggregate metrics
match the previous 2020 run. The 2021 editor log has three successful Tundra build
steps, a Build Report, zero shader errors, and zero C# errors. Unity also logged
licensing-client diagnostics which it ignored while continuing to a successful
build.

Evidence files:

- `source-manifest.json` — exact fresh include hashes, wrapper hashes, unchanged
  negative-control hashes, and harness source hashes.
- `editor-build.log` — 2021.3.45f2 build output.
- `gpu-report.json` — runtime Metal result.
- `player-file-manifest.json` — hashes/sizes for the temporary ARM64 player bundle
  before cleanup.
- `run-report.json` — editor/player versions, hashes, exact command, result metrics,
  and scope limits.

The generated project, Library/cache, and app were removed after capturing this
evidence. `prepare_native_probe_2021.py` recreates the minimal source project without
touching `unity-full`; choose fresh `--project` and `--evidence` paths when repeating
the run. From the repository root, reproduce with:

```sh
python3 shaders/color-path/prepare_native_probe_2021.py \
  --project color-path-probe-unity2021-repro \
  --evidence shaders/color-path/native-proof-2021-repro
UNITY_EDITOR="$PWD/.tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/MacOS/Unity"
"$UNITY_EDITOR" -batchmode -quit \
  -projectPath "$PWD/color-path-probe-unity2021-repro" \
  -executeMethod BuildColorProbe.Build \
  -logFile "$PWD/shaders/color-path/native-proof-2021-repro/editor-build.log" -noUpm
COLOR_PROOF_REPORT="$PWD/shaders/color-path/native-proof-2021-repro/gpu-report.json" \
  "$PWD/color-path-probe-unity2021-repro/output/ColorPath.app/Contents/MacOS/Color Path Probe"
```

The editor is
`/Users/frityet/Projects/NativeH3VR/.tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/MacOS/Unity`.
The original 2020 report and logs remain unchanged.

This is a focused four-variant Uber shader test. It does not establish broad shader
parity, integration behavior for every overlay, or full-game visual approval.
