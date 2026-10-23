# Recover the 67 AVPro MediaPlayer serialized components

AssetRipper exported these components with engine headers only after reporting `EndOfStreamException`. The installed game's original binary data still contains all settings. This recovery reads the original assembly's actual serializable fields using Cecil, decodes all 67 source objects, and writes the recovered values into the **working `unity-full` only**. Installed game files, pristine exports and the smaller `unity` project are not modified.

## Result

- 67 original MonoBehaviour payloads, each 816 bytes, identified by their pointer to `globalgamemanagers.assets:1089` (`RenderHeads.Media.AVProVideo.MediaPlayer`).
- 64 components map to their preserved original path IDs in 32 scenes. The other three map by unique, complete GameObject/Transform hierarchy in `ItemSpawnerPanelV2.prefab` and `ToolPanel_ItemSpawner.prefab`; names alone are not used.
- All 67 payloads independently encode back to the original bytes exactly. No leftover bytes or assumed default settings.
- All retain `MediaSource.Reference`, linked to `resources.assets:32405`, the exported `Splash_MainMenu` MediaReference. Its actual file is `TutorialVideo/Splash/MainmenuLoop.mp4`, present in original and working StreamingAssets. Playback, looping, audio, platform decoder settings and the event mask are restored. Original persistent event lists are empty; no event handlers are invented.
- An isolated Unity 2020.3.49f1 import deserializes 67 extracted components and checks **10,452 primitive, array and object-reference properties, with zero errors** against the decoded original values. The old UnityEvent `m_TypeName` string is migrated to the renamed game assembly in YAML but is no longer a serialized property in Unity 2020; it is intentionally excluded from those property assertions.
- 34 modified working files match their validated staged SHA256s. Previous versions are backed up under `build/video/serialization/backups`.

Evidence is under `build/video/serialization`: `inventory.json`, `schema.json`, `decoded.json`, `restoration-plan.json`, `unity-validation.json`, and `restoration-applied.json`. The latter records every original file/pathID/hash and exact target YAML file/component ID.

## Reproduction

From repository root, against an **unpatched** working full export:

```sh
dotnet run --project platform/video/serialization/schema/Schema.csproj -c Release
audit-assets/.venv/bin/python platform/video/serialization/inventory.py
python3 platform/video/serialization/decode.py
audit-assets/.venv/bin/python platform/video/serialization/restore.py
platform/video/serialization/validate.sh
python3 platform/video/serialization/apply.py
```

The last step requires all 67 Unity component and 10,452 property checks to pass, then verifies every input and staged file hash before applying. `restore.py` deliberately rejects a component with existing authored/recovered fields, so repeating recovery cannot silently overwrite later edits. To revalidate an already staged recovery, run only `validate.sh`. Original backups permit an explicit rollback; no automatic rollback modifies later user changes.

The byte codec is scoped to these observed MediaPlayer payloads, not a general Unity serializer. Its arrays are all empty in these 67 objects. Other script types and versions require their own schema and verification. The isolated import confirms serialized values and linked asset identity; actual tutorial interactions and playback inside every game scene remain integration tests.
