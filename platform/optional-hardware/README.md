# ForceTube optional-hardware JIT isolation

The existing macOS guard disables unsupported ForceTube commands and throws for unsupported data queries, with one honest feature-unavailable warning. Nevertheless, Mono resolves the original Windows P/Invoke imports while JIT-compiling those guarded methods, before evaluating the guard. This causes missing-library fallback diagnostics even though no command executes.

`patch/Program.cs` applies after the VR patch to the latest composed FirstPass assembly. It moves each of the 14 native call sites across seven guarded methods behind a private static `NoInlining` managed thunk. Existing guards, signatures, native P/Invoke definitions and supported-platform behavior remain unchanged. On macOS the thunks are never compiled or called. No plugin is fabricated and no unavailable query reports success. Unexpected or duplicate patch inputs fail explicitly.

```
dotnet run --project platform/optional-hardware/patch/Patch.csproj -- INPUT/NativeH3VR.FirstPass.dll OUTPUT/NativeH3VR.FirstPass.dll
bash platform/optional-hardware/test.sh
```

The test uses the original recovered ForceTube interface and equivalent existing guards, compiled under the same native Mono runtime. The unpatched unavailable path reproduces eager missing-native-library resolution. The patched unavailable path produces zero DllImport attempts and both data queries still throw. The supported branch calls an explicitly isolated ARM64 native fixture, validating all seven actual native entry points, arguments and query results. This checks boundary behavior, not physical ForceTube hardware support.

The patch was also applied to the actual staged FirstPass assembly into `build/optional-hardware/NativeH3VR.FirstPass.dll`, with its detailed report beside it. No full project assets were edited. Final whole-player startup remains necessary to confirm clean diagnostics in the built Unity player.
