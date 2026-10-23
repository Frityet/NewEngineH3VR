# NewEngineH3VR Steam launch

Package the generated Unity app as `dist/NewEngineH3VR.app`. `script/package_native.py` preserves its original ARM64 Unity executable as `Contents/MacOS/NewEngineH3VRPlayer`, installs `build/launch/NewEngineH3VR` as `Contents/MacOS/NewEngineH3VR`, and sets `CFBundleExecutable`, bundle name and display name to `NewEngineH3VR`. It also copies `platform/launch/steam_appid.txt` to `Contents/Resources/steam_appid.txt` and signs the completed app.

A fresh Unity build may already call its engine `NewEngineH3VR`; packaging identifies the engine through its UnityPlayer linkage before replacing that public entrypoint. Repackaging retains the separate player. The earlier `NativeH3VR` launcher/player pair is recognized only for migration; missing or conflicting engines are rejected.

The native C launcher sets SteamAppId/SteamGameId to 450540, changes to the bundle Resources directory, and execs the actual native Unity player. Normal launches select the live native headset runtime. An explicit `NATIVEH3VR_OPENXR=synthetic` override is retained for automated tests; invalid modes are rejected. Packaging rebuilds the ARM64 launcher from the current source via `build.py`.

This is Valve's development AppID path. It does not replace authentication, ownership checks, or the Steam client. The original game's `RestartAppIfNecessary(450540)` and `Init()` stay active.

The real native Steam probe, run with these environment values and the AppID file as current directory, returned `restart_required=false`, initialized, confirmed the signed-in account owns 450540, and received a real successful stats callback. Evidence: `build/steamworks/probe.log`. Current packaged-player verification and its limits are recorded in [the OptiH3VR checkpoint](../../reports/OPTIH3VR.md).

## Live profiling requests

For a future session, arm profiling while launching the interactive range:

```sh
PROFILE_ROOT="$PWD/build/live-profile-$(date +%Y%m%d-%H%M%S)"
OPTIH3VR_ENABLED=1 NEWENGINEH3VR_PROFILE_DIR="$PROFILE_ROOT" \
  NEWENGINEH3VR_PROFILE_SECONDS=20 ./script/run_headset_demo.sh
```

This arms the controller; it does not record or supply game controls. Wait for
`NEWENGINEH3VR_PROFILE_READY`, enter the intended scene and prepare the busy
workload before requesting a capture. In another terminal, inspect that session's
`ready-PID-ID.json` under the profile root. For the recorded interactive session,
`build/optih3vr/current-live-session.json` identifies the player PID and session
directory; its ready file is under that directory's `profiles/` folder.

Use the ready file's **profiler** `runId` for requests. The session record's
`runId` is the game's ID, exposed as `gameRunId` in the ready file. They differ.
After confirming the intended player's PID and that the workload is ready, set
the two per-session values below and run the request once:

```sh
NEWENGINE_READY=/absolute/path/to/ready-PID-ID.json \
NEWENGINE_EXPECTED_PID=PLAYER_PID python3 - <<'PY'
import json, os, pathlib, subprocess, uuid
ready = json.loads(pathlib.Path(os.environ['NEWENGINE_READY']).read_text())
expected_pid = int(os.environ['NEWENGINE_EXPECTED_PID'])
assert ready['pid'] == expected_pid and ready['runId']
assert not ready['capturing'] and ready['state'].startswith('ready')
command = subprocess.check_output(
    ['ps', '-p', str(expected_pid), '-o', 'comm='], text=True).strip()
assert pathlib.Path(command).name == 'NewEngineH3VRPlayer'
target = pathlib.Path(ready['startRequestPath'])
assert target.parent == pathlib.Path(ready['runDirectory']) and not target.exists()
request = dict(pid=expected_pid, runId=ready['runId'], seconds=20,
               label='User-ready busy Sosig sandbox')
temporary = target.with_name('.start-' + uuid.uuid4().hex + '.tmp')
temporary.write_text(json.dumps(request) + '\n')
temporary.replace(target)
print('Requested bounded capture for', expected_pid, ready['runId'])
PY
```

The capture stops automatically and restores the previous profiler settings
without closing gameplay. Wait for the capture's `summary.json` to report
`state=complete` and `settingsRestored=true`, with no restoration error, before
reading its binary log. See [offline analysis](../profile-analysis/README.md).
Recording adds overhead; marker times include waits and do not establish an
overall FPS improvement or physical scanout rate. Compatibility environment keys,
serialized `NativeH3VR` identities and historical evidence paths stay unchanged.
