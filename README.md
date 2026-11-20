# Unity 6.7 validation

This branch contains a standalone Unity engine fixture and read-only tools for recording the installed Steam release of H3VR. Its Unity project targets `6000.7.0b10`. The fixture generates its geometry and textures at runtime and tests them on the GPU.

## Build and run the fixture

Use Python 3.9 or newer and a licensed Unity 6.7 editor with build support for each requested platform. Run the player in a graphical session with Metal on Apple Silicon, Direct3D 11 on x86-64 Windows, or Vulkan on x86-64 Linux.

```sh
python3 -B tools/engine_validation.py --output results/unity-6.7
python3 -B tools/engine_validation.py --platform all --build-only --output results/builds
```

Each command takes a new output directory. Use `--unity` with the editor executable or macOS app when Unity is installed outside the default Unity Hub location. Reports, captures and logs go in the output directory. Temporary projects and players are removed automatically. `--keep-player` retains the player for transfer or standalone runs.

The fixture checks depth occlusion, blending, face culling, point sampling, camera clipping and layers, synthetic stereo parallax, transforms and physics. `--reference` compares the rendered frames against an existing fixture capture directory. It allows a mean difference of one level per RGB channel and at most 0.5% of pixels with a difference above eight levels in any channel. H3VR gameplay, rendering and headset parity remain unverified.

## Record the installed Steam release

```sh
python3 -B tools/stock_baseline.py --appmanifest /path/to/appmanifest_450540.acf
python3 -B tools/stock_baseline.py --player-log /path/to/output_log.txt
```

You can pass both arguments together. The first reads the installed build ID. The second summarizes engine and API initialization markers from an existing stock Unity log. Frame delivery requires separate evidence.

## Source checks

```sh
python3 -B -m unittest discover -s tests
python3 -B tools/audit_sources.py
python3 -B tools/audit_sources.py --staged
```

The audit reads Git objects. It checks allowed paths, regular-file modes, UTF-8 text and a 1 MiB limit per file. Text provenance requires review before publishing.

GPL-3.0. See [LICENSE](LICENSE).
