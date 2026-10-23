# Input lookup cache candidate checks

Run from the workspace root, with a fresh build directory:

```sh
python3 native/input-lookup-tests/run.py --output build/input-lookup-candidate
```

This compiles the frozen original and current adapter sources together for pure
.NET host comparisons using actual game input structs, Valve JSON and Unity
managed modules. It also builds isolated C#6 adapter DLLs with and without
`NEWENGINE_UNITY_2021`, checks protected method bodies/field layouts, and records
input/reference/artifact hashes. It never runs Unity, touches a native runtime,
or writes an app. Valid-pose Unity native transforms and physical haptics require
separate runtime acceptance; the pure tests explicitly avoid invoking them.

The gated candidate caches action-name/set-handle pairs after each manifest
registration batch, including partial failures and late registration of sets.
Old handles, original types, malformed query exceptions, action activation,
polling, input freshness/focus edges and retained snapshots are preserved.
Active-set HashSets and snapshot arrays still have their original ownership.
ABI sizes use a nested per-type static cache. Existing adapter/bootstrap fields
remain unchanged; new state lives in nested types.

`NATIVEH3VR_OPENXR` becomes a launch/session snapshot in the gated branch. A new
`CreateCore` refreshes it before setting `Synthetic`. Changing the environment
mid-session does not switch the backend. The off-gate branch keeps the original
per-query environment reads. No game guard call site changes are needed.

The normal producer passes `NEWENGINE_UNITY_2021` to **both** the XR and
SteamVR adapter compilations when using Unity 2021. This test compiler explicitly
defines it for the candidate adapter and also compiles the off-gate branch.

Allocation results use .NET's per-thread counter only after it observes a
retained, touched 64 KiB payload. Four alternating original/candidate query
batches distinguish removed warm lookup allocations from one-time cache setup.
The Unity Mono counter previously failed this calibration and supplies no
allocation evidence. Host byte differences are not Unity frame-time/FPS claims,
and the recorded SteamVR callback parent is not an inferred inner call stack.

Frozen originals are exact pre-change source copies, verified by SHA-256 in
`run.py`; the generated test baseline changes only its containing namespace.
Existing engine-proof and gameplay receipts remain historical evidence and are
not rewritten by this test.
