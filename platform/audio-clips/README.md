# Original AudioClip payload preservation

The pristine export contains 13,762 AudioClips (13,737 Vorbis .ogg and 25 PCM16
.wav). Reimporting those with the exported compressionQuality=0 would encode
sound again. These tools instead retain each original FSB5 byte slice and all
original audio settings in Unity 2020 native AudioClip assets.

`identities.py` follows the original AssetRipper collection order and export
filename collision rules and preserves the original GUID. It mapped all 13,762
clips with zero unresolved source objects or exported files. `stage.py` extracts
959,808,864 original encoded bytes into 51 resource files. `native_audio.py` uses
a genuine Unity 2020 ForceBinary AudioClip template, preserving main fileID
8300000 and adding the Unity 2020 non-ambisonic field. Both runtime and editor
resource descriptors refer to the same original bytes. No audio encoder runs.

Original load modes are retained: 13,712 DecompressOnLoad, 29
CompressedInMemory, and 21 Streaming. Channels, frequency, bit depth, length,
subsound, preload/background loading, legacy 3D and compression format remain
unchanged. `verify.py` rereads every native asset and SHA256-checks every resource
slice against the original source extraction manifest.

## Evidence

- `build/audio-clips/preservation-manifest.json`: complete original collection,
  pathID, GUID, metadata, encoded SHA256 and staged destination mapping.
- `build/audio-clips/staged-verification.json`: 13,762 clips, 51 resources,
  zero errors.
- `build/audio-clips/editor-result.json`: real ARM64 Unity 2020 editor loads and
  decodes the original SwallowButter1 FSB; GetData succeeds; peak
  0.321502685546875 and RMS 0.020169938229938.
- `build/audio-clips/player-result.json`: real ARM64 Unity 2020 standalone player
  returns exactly the same sample count, channel count, rate, peak and RMS;
  AudioSource playback advances normally (muted to avoid unsolicited sound).
- `build/audio-clips/player-cases-result.json`: 14 real playback cases cover
  every observed (FSB codec, load mode, channels, frequency) combination, with
  zero errors. Includes PCM16/Vorbis, mono/stereo and 22.05/32/44.1/48 kHz.

The player proof uses an APFS clone of the previously built minimal AudioProbe,
a same-class replacement test assembly, and real Unity 2020 runtime serialized
AudioClip/ResourceManager objects. It establishes native codec compatibility.
The fresh official ARM64 Unity 2021.3.45f2 build subsequently passed the normal
BuildPipeline path. All 14 representative payloads and metadata survived resource
relocation exactly, and the resulting ARM64-only player played every case with
zero errors. See `build-2021-cases-proof.json` and
`player-2021-cases-result.json`. No FSBTool audio encoder was invoked. The full
game build still needs its own validation; this is a representative codec and
load-mode matrix, not a playtest of all 13,762 clips.

## Apply to the working project

With its editor stopped, first run `python3 platform/audio-clips/apply.py`.
It validates all original GUIDs and reports 46,106 audio type-3 references in
3,100 serialized YAML files. `--apply` preserves original importer bundle names
and user data, moves old exported clips and metadata into a reversible backup,
installs staged native assets/resources, and rewrites only the mapped audio
GUID references to type 2. All YAML extensions are scanned, including animation,
mixer and font settings. The original Steam game and pristine exports are never
changed. The script refuses a second apply over an existing backup.

This directory also contains native-dotnet precompiled probe editor/runtime
projects, allowing the probe to avoid Unity's blocked Intel C# compiler. No
installed Unity SDK file is modified.

## Applied working-project state

Applied to `unity-full` after coordinator authorization. The post-apply audit
checks all 13,762 installed assets and GUIDs, all 51 resource hashes, and all
3,100 rewritten YAML hashes; zero errors. Original exports and YAML versions
remain in `build/audio-clips/backup-before-native`. Fresh Unity 2021 builder relocation and the 14-case playback matrix are verified
as described above.

## Mixer migration proof

The same fresh Unity 2021 build includes all 36 recovered AudioMixers and the
unchanged AssetRipper AudioMixerPostprocessor that restores effect parameter
names. `platform/audio-spatial/verify_mixer_semantics.py` compares original and
built graphs by their persistent group/effect/snapshot GUIDs, independently of
compiler array order. It verifies group routing and send targets, ordered effect
chains within each group, sidechain buffers, wet mix, every snapshot parameter
and transition, and exposed parameters/settings. All 36 match, including the
multiplicity of equivalent mixer copies. The new Unity 2021 explicit
`groupConnections` also matches the original parent/Receive routing; Duck Volume
sends remain sidechain inputs. Evidence: `mixer-2021-semantic-proof.json`.
