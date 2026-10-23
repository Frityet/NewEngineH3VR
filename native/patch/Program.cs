using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
if (args.Length != 3)
    throw new ArgumentException("usage: Patch input-directory adapter.dll output-directory");
string input = Path.GetFullPath(args[0]), adapterPath = Path.GetFullPath(args[1]),
       output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(input);
resolver.AddSearchDirectory(output);
resolver.AddSearchDirectory(Path.GetDirectoryName(adapterPath));
resolver.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/Managed/UnityEngine");
var options = new ReaderParameters { AssemblyResolver = resolver };
using var adapter = AssemblyDefinition.ReadAssembly(adapterPath, options);
using var xr = AssemblyDefinition.ReadAssembly(Path.Combine(output, "NativeH3VR.XR.Managed.dll"),
                                               options);
var bootstrap = adapter.MainModule.GetType("NativeH3VR.NativeSteamVRBootstrap");
var enabled = bootstrap.Methods.Single(m => m.Name == "get_Enabled");
var adapterTypes = new Dictionary<string, TypeDefinition> {
    { "CVRInput", adapter.MainModule.GetType("NativeH3VR.NativeSteamVRInput") },
    { "CVRSystem", adapter.MainModule.GetType("NativeH3VR.NativeSteamVRSystem") },
    { "CVRCompositor", adapter.MainModule.GetType("NativeH3VR.NativeSteamVRSystem") },
    { "CVRChaperone", adapter.MainModule.GetType("NativeH3VR.NativeSteamVRSystem") }
};
var changes = new List<object>();
int projectionResetCount = 0;
var errors =
    new Dictionary<string, int> { { "EVRInputError", 13 },          { "EVRCompositorError", 1 },
                                  { "EVROverlayError", 23 },        { "EVRRenderModelError", 200 },
                                  { "EVRScreenshotError", 1 },      { "EVRSettingsError", 5 },
                                  { "ETrackedPropertyError", 4 },   { "EVRApplicationError", 100 },
                                  { "EVRTrackedCameraError", 100 }, { "EVRSpatialAnchorError", 1 },
                                  { "EVRIOBufferError", 100 } };
bool Same(MethodDefinition target, MethodDefinition helper) =>
    target.Name == helper.Name && target.ReturnType.FullName == helper.ReturnType.FullName &&
    target.Parameters.Select(p => p.ParameterType.FullName)
        .SequenceEqual(helper.Parameters.Select(p => p.ParameterType.FullName));
foreach (string name in new[] { "NativeH3VR.FirstPass.dll", "NativeH3VR.Game.dll" }) {
    using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(input, name), options);
    var module = assembly.MainModule;
    var ppLayer = module.GetType("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
    if (ppLayer != null) {
        var projectionHelper = xr.MainModule.GetType("NativeH3VR.NativeEyeProjection");
        foreach (var method in ppLayer.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference call &&
                    call.DeclaringType.FullName == "UnityEngine.Camera" &&
                    call.Name == "ResetProjectionMatrix") {
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(
                        projectionHelper.Methods.Single(m => m.Name == "ResetProjection"));
                    projectionResetCount++;
                    changes.Add(new { assembly = name, method = method.FullName,
                                      kind = "native-eye-preserve-original-projection-reset" });
                }
    }
    void Guard(MethodDefinition method, List<Instruction> injected, string kind) {
        if (!method.HasBody)
            throw new InvalidOperationException("No body " + method.FullName);
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(enabled)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        foreach (var instruction in injected)
            il.InsertBefore(first, instruction);
        changes.Add(new { assembly = name, method = method.FullName, kind });
    }
    List<Instruction> Forward(MethodDefinition method, MethodDefinition helper,
                              bool includeThis = false) {
        var list = new List<Instruction>();
        if (includeThis)
            list.Add(Instruction.Create(OpCodes.Ldarg_0));
        foreach (var parameter in method.Parameters)
            list.Add(Instruction.Create(OpCodes.Ldarg, parameter));
        list.Add(Instruction.Create(OpCodes.Call, module.ImportReference(helper)));
        list.Add(Instruction.Create(OpCodes.Ret));
        return list;
    }
    // Keep non-native behavior intact; adapt legacy engine identity at its
    // existing call sites so the original game's control-mode selection runs.
    foreach (var type in module.Types)
        foreach (var method in type.Methods.Where(m => m.HasBody))
            foreach (var call in method.Body.Instructions
                         .Where(
                             i =>
                                 i.Operand is MethodReference reference &&
                                 ((reference.Name == "get_model" &&
                                   (reference.DeclaringType.FullName == "UnityEngine.VR.VRDevice" ||
                                    reference.DeclaringType.FullName ==
                                        "UnityEngine.XR.XRDevice")) ||
                                  (reference.Name == "GetVRModel" &&
                                   reference.DeclaringType.FullName == "NativeH3VR.Compatibility")))
                         .ToArray()) {
                method.Body.GetILProcessor().InsertAfter(
                    call, Instruction.Create(OpCodes.Call,
                                             module.ImportReference(bootstrap.Methods.Single(
                                                 m => m.Name == "ResolveLegacyHeadsetModel"))));
                changes.Add(new { assembly = name, method = method.FullName,
                                  kind = "native-runtime-legacy-headset-identity" });
            }
    var forceTube = module.GetType("ForceTubeVRInterface");
    if (forceTube != null)
        foreach (var method in forceTube.Methods.Where(
                     m => m.HasBody &&
                          new[] { "InitAsync", "Kick", "Rumble", "Shoot", "SetActiveResearch",
                                  "GetBatteryLevel", "TempoToKickPower" }
                              .Contains(m.Name))) {
            var first = method.Body.Instructions[0];
            var il = method.Body.GetILProcessor();
            il.InsertBefore(
                first,
                Instruction.Create(OpCodes.Call, module.ImportReference(bootstrap.Methods.Single(
                                                     m => m.Name == "ForceTubeUnavailable"))));
            il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
            if (method.ReturnType.MetadataType == MetadataType.Void)
                il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
            else {
                var exceptionType = new TypeReference("System", "NotSupportedException", module,
                                                      module.TypeSystem.CoreLibrary);
                var ctor = new MethodReference(".ctor", module.TypeSystem.Void,
                                               exceptionType) { HasThis = true };
                ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                il.InsertBefore(
                    first, Instruction.Create(OpCodes.Ldstr,
                                              "ForceTube gunstock data is unavailable on macOS."));
                il.InsertBefore(first, Instruction.Create(OpCodes.Newobj, ctor));
                il.InsertBefore(first, Instruction.Create(OpCodes.Throw));
            }
            changes.Add(new { assembly = name, method = method.FullName,
                              kind = "disable-unavailable-optional-hardware-on-macos" });
        }
    foreach (var type in module.Types.Where(
                 t => t.Namespace == "LIV.SDK.Unity" &&
                      (t.Name == "LIV" || t.Name == "SharedTextureProtocol")))
        foreach (var method in type.Methods.Where(
                     m => m.HasBody &&
                          new[] { "OnEnable", "OnDisable", "Update", "get_IsActive",
                                  "get_TextureWidth", "get_TextureHeight", "SetOutputTexture" }
                              .Contains(m.Name))) {
            var first = method.Body.Instructions[0];
            var il = method.Body.GetILProcessor();
            il.InsertBefore(
                first,
                Instruction.Create(OpCodes.Call, module.ImportReference(bootstrap.Methods.Single(
                                                     m => m.Name == "LIVUnavailable"))));
            il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
            if (method.Name == "OnEnable") {
                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first,
                                Instruction.Create(OpCodes.Call,
                                                   module.ImportReference(bootstrap.Methods.Single(
                                                       m => m.Name == "DisableLIV"))));
            }
            if (method.Name == "get_IsActive")
                il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_0));
            if (method.ReturnType.MetadataType == MetadataType.Int32) {
                var exceptionType = new TypeReference("System", "NotSupportedException", module,
                                                      module.TypeSystem.CoreLibrary);
                var ctor = new MethodReference(".ctor", module.TypeSystem.Void,
                                               exceptionType) { HasThis = true };
                ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                il.InsertBefore(
                    first, Instruction.Create(OpCodes.Ldstr,
                                              "LIV capture dimensions are unavailable on macOS."));
                il.InsertBefore(first, Instruction.Create(OpCodes.Newobj, ctor));
                il.InsertBefore(first, Instruction.Create(OpCodes.Throw));
            } else
                il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
            changes.Add(new { assembly = name, method = method.FullName,
                              kind = "disable-unavailable-optional-liv-on-macos" });
        }
    foreach (var type in module.Types.Where(t => t.Namespace == "Valve.VR")) {
        if (type.Name.StartsWith("CVR")) {
            foreach (var method in type.Methods.Where(m => m.IsPublic && !m.IsConstructor &&
                                                           !m.IsStatic && m.HasBody)) {
                MethodDefinition? helper = null;
                if (adapterTypes.TryGetValue(type.Name, out var helpers))
                    helper = helpers.Methods.FirstOrDefault(m => m.IsStatic && Same(method, m));
                if (helper != null) {
                    Guard(method, Forward(method, helper), "native-adapter");
                    continue;
                }
                var list = new List<Instruction> {
                    Instruction.Create(OpCodes.Ldstr, type.Name + "." + method.Name),
                    Instruction.Create(OpCodes.Call,
                                       module.ImportReference(bootstrap.Methods.Single(
                                           m => m.Name == "Unsupported")))
                };
                bool hasError = false;
                foreach (var parameter in method.Parameters.Where(
                             p => p.ParameterType.IsByReference)) {
                    var element = ((ByReferenceType)parameter.ParameterType).ElementType;
                    if (errors.TryGetValue(element.Name, out int error)) {
                        list.Add(Instruction.Create(OpCodes.Ldarg, parameter));
                        list.Add(Instruction.Create(OpCodes.Ldc_I4, error));
                        list.Add(Instruction.Create(OpCodes.Stind_I4));
                        hasError = true;
                    } else if (element.IsValueType) {
                        list.Add(Instruction.Create(OpCodes.Ldarg, parameter));
                        list.Add(
                            Instruction.Create(OpCodes.Initobj, module.ImportReference(element)));
                    }
                }
                if (errors.TryGetValue(method.ReturnType.Name, out int result)) {
                    list.Add(Instruction.Create(OpCodes.Ldc_I4, result));
                    list.Add(Instruction.Create(OpCodes.Ret));
                } else if (hasError) {
                    if (method.ReturnType.MetadataType != MetadataType.Void) {
                        var local =
                            new VariableDefinition(module.ImportReference(method.ReturnType));
                        method.Body.Variables.Add(local);
                        method.Body.InitLocals = true;
                        list.Add(Instruction.Create(OpCodes.Ldloca, local));
                        list.Add(Instruction.Create(OpCodes.Initobj, local.VariableType));
                        list.Add(Instruction.Create(OpCodes.Ldloc, local));
                    }
                    list.Add(Instruction.Create(OpCodes.Ret));
                } else if (method.Name == "IsUsingLegacyInput") {
                    list.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    list.Add(Instruction.Create(OpCodes.Ret));
                } else {
                    list.Add(Instruction.Create(OpCodes.Ldstr, "Native H3VR does not implement " +
                                                                   method.FullName));
                    var exceptionType = new TypeReference("System", "NotSupportedException", module,
                                                          module.TypeSystem.CoreLibrary);
                    var exceptionCtor = new MethodReference(".ctor", module.TypeSystem.Void,
                                                            exceptionType) { HasThis = true };
                    exceptionCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                    list.Add(Instruction.Create(OpCodes.Newobj, exceptionCtor));
                    list.Add(Instruction.Create(OpCodes.Throw));
                }
                Guard(method, list, "explicit-unsupported");
            }
        } else if (type.Name == "OpenVR") {
            foreach (var method in type.Methods.Where(m => m.IsPublic && m.IsStatic && m.HasBody)) {
                if (method.Name.StartsWith("get_") && method.ReturnType.Name.StartsWith("CVR")) {
                    var generic = new GenericInstanceMethod(module.ImportReference(
                        bootstrap.Methods.Single(m => m.Name == "Interface")));
                    generic.GenericArguments.Add(method.ReturnType);
                    Guard(method,
                          new() { Instruction.Create(OpCodes.Call, generic),
                                  Instruction.Create(OpCodes.Ret) },
                          "managed-interface");
                } else {
                    var helper =
                        bootstrap.Methods.FirstOrDefault(m => m.IsStatic && Same(method, m));
                    if (helper != null)
                        Guard(method, Forward(method, helper), "native-adapter");
                }
            }
        } else if (type.Name == "SteamVR") {
            var create = type.Methods.Single(m => m.Name == "CreateInstance");
            Guard(create, Forward(create, bootstrap.Methods.Single(m => m.Name == "Create")),
                  "native-session");
            var getter = type.Methods.Single(m => m.Name == "get_enabled");
            Guard(getter,
                  new() { Instruction.Create(OpCodes.Ldsfld,
                                             type.Fields.Single(f => f.Name == "_enabled")),
                          Instruction.Create(OpCodes.Ret) },
                  "native-enabled");
        } else if (type.Name == "SteamVR_Behaviour") {
            // Unity has no XR provider here, so Camera.stereoEnabled is false.
            // Preserve the original once-per-frame PreCull input update, which
            // the legacy OnCameraPreCull otherwise skips for our native eyes.
            var method = type.Methods.Single(m => m.Name == "OnCameraPreCull");
            Guard(method,
                  new() { Instruction.Create(OpCodes.Ldarg_0),
                          Instruction.Create(OpCodes.Call,
                                             type.Methods.Single(m => m.Name == "PreCull")),
                          Instruction.Create(OpCodes.Ret) },
                  "native-camera-pose-update");
        } else if (type.Name == "SteamVR_Action_Skeleton_Source") {
            var method =
                type.Methods.Single(m => m.Name == "UpdateValue" && m.Parameters.Count == 1);
            var il = method.Body.GetILProcessor();
            var helper = adapterTypes["CVRInput"].Methods.Single(
                m => m.Name == "ClearInactiveSkeletonSummary");
            foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret)
                         .ToArray()) {
                ret.OpCode = OpCodes.Ldarg_0;
                il.InsertAfter(ret,
                               Instruction.Create(OpCodes.Call, module.ImportReference(helper)));
                il.InsertAfter(ret.Next, Instruction.Create(OpCodes.Ret));
            }
            changes.Add(new { assembly = name, method = method.FullName,
                              kind = "native-expired-skeletal-summary-reset" });
        } else if (type.Name == "SteamVR_Camera") {
            var method = type.Methods.Single(m => m.Name == "OnEnable");
            var il = method.Body.GetILProcessor();
            // Append after the original rig expansion; redirect each ret so its
            // existing jump targets execute the hook too.
            foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret)
                         .ToArray()) {
                ret.OpCode = OpCodes.Ldarg_0;
                il.InsertAfter(ret,
                               Instruction.Create(OpCodes.Call,
                                                  module.ImportReference(bootstrap.Methods.Single(
                                                      m => m.Name == "CameraEnabled"))));
                il.InsertAfter(ret.Next, Instruction.Create(OpCodes.Ret));
            }
            changes.Add(
                new { assembly = name, method = method.FullName, kind = "native-camera-hook" });
        }
    }
    var ppContext = module.GetType("UnityEngine.Rendering.PostProcessing.PostProcessRenderContext");
    if (ppContext != null) {
        var projectionHelper = xr.MainModule.GetType("NativeH3VR.NativeEyeProjection");
        // Original SteamVR enables Unity XR, and original PostProcessing
        // disables TAA through this context-level gate. Native OpenXR owns its
        // session outside Unity XR, so scope that same VR policy to its eye
        // camera while leaving desktop/offscreen camera contexts untouched.
        var taa = ppContext.Methods.Single(m => m.Name == "IsTemporalAntialiasingActive" &&
                                                m.Parameters.Count == 0);
        var first = taa.Body.Instructions[0];
        var taaIL = taa.Body.GetILProcessor();
        foreach (var instruction in new[] {
                     taaIL.Create(OpCodes.Ldarg_0),
                     taaIL.Create(OpCodes.Callvirt,
                                  ppContext.Methods.Single(m => m.Name == "get_camera")),
                     taaIL.Create(OpCodes.Call,
                                  module.ImportReference(projectionHelper.Methods.Single(
                                      m => m.Name == "IsEyeCamera"))),
                     taaIL.Create(OpCodes.Brfalse, first), taaIL.Create(OpCodes.Ldc_I4_0),
                     taaIL.Create(OpCodes.Ret)
                 })
            taaIL.InsertBefore(first, instruction);
        changes.Add(new { assembly = name, method = taa.FullName,
                          kind = "native-eye-preserve-original-vr-taa-policy" });
    }
    if (name == "NativeH3VR.Game.dll" &&
        xr.MainModule.GetType(StereoPackingPatch.HelperType) != null) {
        var packedStereo = StereoPackingPatch.Apply(assembly, xr);
        changes.Add(new { assembly = name, method = packedStereo.FullName,
                          kind = "newengine-separate-eye-postprocess-packing" });
        if (xr.MainModule.GetType(ScopePairPatch.HelperType) != null) {
            var scopePair = ScopePairPatch.Apply(assembly, xr);
            changes.Add(new { assembly = name, method = scopePair.FullName,
                              kind = "newengine-scope-pair-activation",
                              callsites = ScopePairPatch.Sites });
        }
    }
    assembly.Write(Path.Combine(output, name));
}
if (projectionResetCount != 2)
    throw new InvalidOperationException(
        "Expected two original PostProcessLayer projection reset calls, found " +
        projectionResetCount);
File.Copy(adapterPath, Path.Combine(output, Path.GetFileName(adapterPath)), true);
File.WriteAllText(Path.Combine(output, "vr-patch-manifest.json"),
                  JsonSerializer.Serialize(changes,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(
    $"Patched {changes.Count} managed entry points. Native paths require explicit NATIVEH3VR_OPENXR=synthetic|live. Unsupported paths return errors or throw; no fake success.");
