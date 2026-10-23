# Unity managed plugin self-reference repair

Cecil's `ImportReference` imports types in an external helper's signature. When
the helper refers back to the caller's own types, this can introduce an
`AssemblyRef` to the caller itself. Unity's PluginImporter rejects such an
assembly even though ordinary metadata resolution succeeds. In the failed
bundle build this rejected Game and FirstPass and their dependents, producing
13,097 missing-script warnings from otherwise valid prefabs.

`Program.cs` changes only these self-scoped TypeRefs to the current module's
existing definitions and removes the redundant self AssemblyRef. It refuses
conflicting assembly identities and undefined local types. The output is read
back to verify local resolution and an unchanged fingerprint of every method's
signature, attributes, locals, IL instructions and exception handlers.

`native/build_managed.py` runs the repair immediately after importing the native
adapter. This also covers direct adapter builds outside the composition script.
The composition verifier now rejects any surviving self AssemblyRef. It was
tested against the failed composition (two errors) and the repaired composition
(zero errors).

Evidence:

- `build/scene-integrity/self-reference-rejection.json`: validator rejects the
  two originally broken assemblies.
- `build/composed/final/local-reference-repair.json`: 24 FirstPass and 3 Game
  TypeRefs rebound locally, with method fingerprints unchanged.
- `build/composed/final/verification.json`: complete target Unity API validation.

The correction changes metadata scope, not gameplay logic or script identities.
Editor loading and prefab preflight remain required before trusting bundles.
