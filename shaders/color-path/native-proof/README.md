# Corrected-emitter native Metal probe

The tested shaders are the actual two equivalent no-bloom and two bloom include variants from the freshly regenerated `Hidden/PostProcessing/Uber` output. The existing `Broken` controls came from the pre-fix accepted generated source, and `SwizzleOnly` controls retained their one-cause negative correction. The harness maps DXBC `LOG`/`EXP` to `log2`/`exp2` and supplies the stereo scale-offset array expected by Unity's older editor shader include.

Reproduce after the package candidate exists:

```sh
python3 shaders/color-path/prepare_native_probe.py
/Applications/Unity/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath "$PWD/color-path-probe-swizzle" -executeMethod BuildColorProbe.Build -logFile "$PWD/color-path-probe-swizzle/output/emitter-build.log"
COLOR_PROOF_REPORT="$PWD/color-path-probe-swizzle/output/emitter-gpu-report.json" "$PWD/color-path-probe-swizzle/output/ColorPath.app/Contents/MacOS/Color Path Probe"
```

The captured run used Unity 2020.3.49f1 (the project descriptor requests 2021.3.45f2), Metal on Apple M5 Max. The captured result is 32/32 corrected rows within `6.09e-05`, while all 32 original bad-emission and all 32 swizzle-only negative rows were detected. See `emitter-gpu-report.json` and `emitter-build.log`.
