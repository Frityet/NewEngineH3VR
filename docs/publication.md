# Source-only publication

Publish authored converter code, tests, project files and documentation only. Do not commit or attach extracted game source, managed/native game binaries, Unity files, original or recovered shaders, audio, textures, meshes, scenes, AssetBundles, generated Unity projects, player applications or shader-recovery output.

The root `.gitignore` admits only explicit source/documentation paths and the public AssetRipper submodule. `check-publication` independently inspects the staged Git index and actual blob contents. It rejects unknown paths, non-text content, files over 1 MiB, symlinks and unresolved merges. Its sole submodule exception is `extern/AssetRipper` at the reviewed upstream commit `1ac666f47d8e9dedf96afb0b914c70d7656151ea`, with the exact public HTTPS URL in `.gitmodules`. Other submodules, revisions and URLs fail. Submodule contents are fetched from upstream, not included as copied assets in this repository. Run the check after staging and before committing:

```sh
dotnet run --project src/H3VR.Converter -- check-publication
```

A text-file extension does not establish authorship. Review new source, embedded patches and Git history for provenance too. This repository contains generic migration algorithms and locally generated runtime adapters; installed game and SDK dependencies are supplied by each user. Keep screenshots, logs containing extracted data, shader dumps and build artifacts out of public releases unless separately reviewed.
