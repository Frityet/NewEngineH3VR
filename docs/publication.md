# Publishing safely

Only our own code, tests, project files and docs go in this repo. Never commit anything that came from the game or from Unity, such as:

- game code or binaries (managed or native)
- Unity files, generated Unity projects or built players
- shaders (original or recovered) and shader recovery output
- audio, textures, meshes, scenes or AssetBundles

## Checking before you commit

Stage your changes, then run:

```sh
dotnet run --project src/H3VR.Converter -- check-publication
```

This looks at what's actually staged in Git, not just at `.gitignore`. It fails on:

- paths that aren't on the allowed list
- binary files, or files over 1 MiB
- symlinks
- unresolved merge conflicts
- any submodule other than `extern/AssetRipper` at commit `1ac666f47d8e9dedf96afb0b914c70d7656151ea`, using the exact public HTTPS URL in `.gitmodules`

The submodule's contents come from upstream and aren't copied into this repo.

## Things the checker can't catch

A file being plain text doesn't mean we wrote it. Check new code, patches and Git history for where they came from.

Screenshots, logs with extracted data, shader dumps and build output also stay out of public releases unless someone has reviewed them.
