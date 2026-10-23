The recovered EC79A2 scope contains an invented `ZClip Off` state. The original
Unity 5.6.7f1 serialized shader has no `zClip` field in either of its two pass
states. The local AssetRipper 1.0.15 source explains the discrepancy:

- `SerializedShaderStateExtensions.cs:9` evaluates absent `ZClip` as zero.
- `Enums/Shader/SerializedShader/ZClip.cs` defines zero as `Off`, one as `On`.
- `IO/SerializedExtensions.cs:379` writes a `ZClip` command when the result is
  not `On`, so the absent legacy field becomes an explicit `ZClip Off`.

Unity's [2021.3 ZClip manual](https://docs.unity3d.com/2021.3/Documentation/Manual/SL-ZClip.html)
documents clipping as the default. Restoring that default is a source migration
repair; missing serialized data is not evidence that clipping was authored off.
The source inspection establishes the exporter error. Physical scope acceptance
and any broader visual implications require separate runtime evidence.

`repair_legacy_depth_clip.py` produces isolated candidates. It admits only the
nine inventory-bound `Unlit/PIPScope` shader assets. For every selected shader,
it reads the original Steam bundle without writing it, requires Unity 5.6,
matches serialized file/path ID/name and ordered SubShader/pass identities, and
checks the actual original pass-state keys. Only a literal candidate `ZClip Off`
in a pass with an absent original field changes to `ZClip On`. Explicit original
states, including property-backed states, remain untouched. Candidate property
expressions remain untouched. Ambiguous inherited/duplicate commands or mismatched
pass identities fail before output is created. Reversing the admitted token
edits must reproduce every input byte exactly.

Use the UnityPy environment and a new candidate directory:

```sh
audit-assets/.venv/bin/python shaders/repair_legacy_depth_clip.py \
  --input-dir shaders/color-path/semantic-v3-packaged \
  --shader Unlit_PIPScope_6.shader \
  --output-dir build/legacy-depth-clip-pip-ec79
```

Omit `--output-dir` for an audit without writes. Repeat `--shader` for another
inventory PIP asset, or omit it to inspect the nine PIP assets. Output cannot be
inside an input tree, original game/Steam library, staged `unity-full`, or app
bundle. Existing output directories are rejected. No stage/build/install hook
calls this tool automatically. The global exporter has not been modified.

The EC79A2 proof is bound to original shader `-3051070195593120036` in serialized
file `CAB-d98205cd3e60321f23684452d96b0f22`, original bundle
`StreamingAssets/assets_resources_objectids_weaponry_ar15` (SHA256
`f89a56e00a699639948c246d6ef42f84d80bdf3d7d484058b061e707eb965fa2`). Its
`FORWARDBASE` and `SHADOWCASTER` pass fields are both absent. Current exported
asset GUID `dad69e77fef66964c982ff58f723a8ba` selects
`Unlit_PIPScope_6.shader`. The candidate changes exactly those two commands;
its SHA256 is `77d8d74054788f530a03f9526532e520636be5eda52995f041f730a18ac8eeb1`.
The adjacent `depth-clip-repair.json` records source, tool, inventory and exporter
source hashes, each original pass-state hash, input/output hashes and byte offsets.

Validation: `python3 -m unittest shaders/test_repair_legacy_depth_clip.py` passes
16 tests covering authored Off/property/null states, candidate property preservation,
idempotence, pass mismatch/ambiguity rejection, comments/HLSL/CRLF/non-ASCII byte
preservation and output protection. The EC79 original-state/candidate run succeeded.
These checks do not establish full-game visual parity or justify changing every
shader family.
