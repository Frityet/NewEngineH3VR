# Native Unity platform integration probe

Run `platform/test-unity.sh [composed-managed-directory]` from the project root after building the Steam, FMOD and video adapters. This creates a separate small Unity project and arm64/Metal development player. It never imports or edits the recovered main Unity project.

The probe calls the original patched Steamworks and FMOD managed APIs and the original AVPro MediaPlayer factory under Unity's actual arm64 Mono runtime. It authenticates with the signed-in Steam client, checks H3VR ownership and receives a real stats callback; loads the seven original FMOD banks and mixes a real TAH2 event; then decodes a generated red/blue H.264 fixture, checks its Metal texture orientation, seeks to1.5 seconds, and pauses.

The successful result is `build/platform-probe/result.json`; the decoded video pixels are `build/platform-probe/video-frame.png`. Actual executable architecture is checked separately with `file` (`arm64`). The fixture is synthetic and no-speaker FMOD output is used, so this is technical integration evidence, not a gameplay or listening approval. All state flags passed on Unity2020.3.49f1 and the Apple M5 Max GPU.

The first run exposed a real seeking-state race: Unity's seek-completed event arrived before its public playback time advanced. The adapter now retains seeking state until both completion and the target time are observed; the repaired probe passed.
