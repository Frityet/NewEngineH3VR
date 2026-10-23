# Validation and remaining work

The C# rewrite targets Unity **2021.3.45f2**. These checks were performed on an
Apple Silicon Mac. They are distinct from a successful converted game build or
a headset playtest.

## Checks performed

- The .NET solution builds with warnings treated as errors. Synthetic tests cover
  path boundaries, copy isolation, serialized script migration, original asset
  bundle addresses, input fingerprints, workspace locking and publication rules.
- The OpenXR patcher has a synthetic Cecil integration test. Against the local
  game, it blocks 10 native imports, forwards 47 methods and records 100 methods
  with explicit unsupported behavior. The migration preserves 30,388 serialized
  or public field definitions. These are structural checks, not proof that every
  gameplay path uses a supported API.
- Post-patch managed verification resolves 1,868 type references, 98,545
  Unity/compatibility call sites and 14,582 engine field references with zero
  errors against the pinned editor's managed and Mono reference assemblies.
- The actual CLI completed extraction-input verification, managed migration and
  project preparation against a local installation and an existing shader
  overlay. The project report contains 2,351 script bindings, 292,081 rewritten
  references and 41 execution-order entries. The bundle plan resolves 4,387
  original asset addresses across 18 bundles with no unresolved addresses.
- Unity 2021.3.45f2 compiled the final editor and OpenXR feature sources in an
  isolated project. OpenXR configuration selected the loader, controller profiles
  and session-focus feature. This used OpenXR 1.13.0, XR Management 4.4.0,
  Input System 1.6.3, XR Core Utils 2.1.1 and Legacy Input Helpers 2.1.7.
- Raw shader recovery extracted 390 serialized Shader objects and produced 277
  non-dummy shader source files. The exporter can be provisioned from a fresh
  pinned public source checkout; its local web service was started and checked.
  This does not establish shader correctness.
- The relocated native bridge in ValveIndexMacOS passed its synthetic stereo,
  eye-query, core and action checks. That bridge is separate from this converter's
  Unity XR provider contract.

## Not established by these checks

A full new player build, complete scene/prefab import, headset presentation,
controller interaction, finger tracking, game parity and sustained performance
have not been established for the rewritten pipeline. The available local
editor has the macOS build module; the default Windows target needs its own
installed build module. The old working local player predates this rewrite and
is not evidence for the new adapter.

Faithful shader restoration remains the largest fresh-installation gap. Raw
shader output needs identity matching, semantic review and Unity/graphics
validation; it is not a validated overlay. See [shader recovery](shader-recovery.md).
The converter stops on detected dummy shaders, unresolved bundle addresses,
managed API failures, new missing scripts and shader/compiler errors. Passing
those checks still does not prove visual or gameplay equivalence.

Unity's stock OpenXR provider does not supply a macOS player backend. A native
OpenXR runtime alone cannot replace a Unity XR display/input provider or the
game's other native plugins. See [platform integrations](platform-integrations.md).

All local game assets, managed outputs, reports containing game-derived data,
and compiled players remain outside the published source tree. CI uses
synthetic fixtures and does not download or require H3VR.
