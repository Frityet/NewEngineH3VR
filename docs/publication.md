# Source-only publication

Publish authored converter code, tests, project files and documentation only. Do not commit or attach extracted game source, managed/native game binaries, Unity files, original or recovered shaders, audio, textures, meshes, scenes, AssetBundles, generated Unity projects, player applications or shader-recovery output.

The root `.gitignore` admits only explicit source/documentation paths. `check-publication` independently inspects the staged Git index and actual blob contents. It rejects unknown paths, non-text content, files over 1 MiB, symlinks, submodules and unresolved merges. Run it after staging and before committing:

```sh
dotnet run --project src/H3VR.Converter -- check-publication
```

A text-file extension does not establish authorship. Review new source, embedded patches and Git history for provenance too. This repository contains generic migration algorithms and locally generated runtime adapters; installed game and SDK dependencies are supplied by each user. Keep screenshots, logs containing extracted data, shader dumps and build artifacts out of public releases unless separately reviewed.
