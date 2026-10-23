# Source-only publication

The Git inventory is explicit: `.gitignore` denies everything except reviewed
files listed in `config/public-files.json`. The publication checker independently
checks the staged tree, including file modes, UTF-8 content and permitted types.
Run it after staging and before every commit intended for publication.

Included: authored adapters, migration/extraction scripts, diagnostic source,
build configuration, documentation, and patches to separately obtained tools.
The AssetRipper patches modify the exporter; they do not contain recovered game
shader bytecode. The recovery settings file is configuration, not game data.

Excluded: recovered/decompiled game source, original or patched game assemblies,
SDK/tool trees, copied upstream exporter source files, historical vendored shader
packages, Unity scenes/materials/prefabs, shader programs/includes/bytecode,
textures, video/audio, data dumps, generated manifests/reports, builds and apps.
Generated `.cs` files are not automatically allowed simply because they are text.

No base-game content is intentionally included. The gate detects file-policy
violations; human provenance review is still required for new source code or
embedded data. Do not add broad extension exceptions or use `git add -f` to ship
local recovery products. Do not attach the generated app to a public release.

Old video-candidate experiments and editor source backups were moved to a local
sibling archive during cleanup. Active local recovery/build inputs were retained
in place to preserve the working port, but are absent from the published tree.
