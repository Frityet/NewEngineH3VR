using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Diagnostics;
using System.Text.Json;

var root = FindRoot();
var temp = Path.Combine(Path.GetTempPath(), "nativeh3vr-openxr-tests-" + Guid.NewGuid().ToString("N"));
var input = Path.Combine(temp, "input");
var output = Path.Combine(temp, "output");
Directory.CreateDirectory(input);
try
{
    File.WriteAllBytes(Path.Combine(input, "UnityEngine.CoreModule.dll"), CreateEmptyAssembly("UnityEngine.CoreModule"));
    File.WriteAllBytes(Path.Combine(input, "NativeH3VR.FirstPass.dll"), CreateFirstPass());
    File.WriteAllBytes(Path.Combine(input, "NativeH3VR.Game.dll"), CreateGame());
    var adapter = Path.Combine(temp, "NativeH3VR.OpenXR.dll");
    File.WriteAllBytes(adapter, CreateAdapter());

    var patchProject = Path.Combine(root, "tools", "OpenXRPatch", "OpenXRPatch.csproj");
    var psi = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--project");
    psi.ArgumentList.Add(patchProject);
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add(input);
    psi.ArgumentList.Add(adapter);
    psi.ArgumentList.Add(output);
    psi.ArgumentList.Add("--unity-engine");
    psi.ArgumentList.Add(input);
    using var process = Process.Start(psi) ?? throw new Exception("Could not launch patcher");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Check(process.ExitCode == 0, "patcher failed: " + stdout + stderr);

    using var patched = AssemblyDefinition.ReadAssembly(Path.Combine(output, "NativeH3VR.FirstPass.dll"));
    var module = patched.MainModule;
    Check(module.AssemblyReferences.All(reference => reference.Name != module.Assembly.Name.Name),
          "patch introduced a self-assembly reference");
    var cvrInput = module.GetType("Valve.VR.CVRInput")!;
    var count = cvrInput.Methods.Single(m => m.Name == "GetBoneCount");
    Check(!count.IsPInvokeImpl && count.Body.Instructions.Any(i => i.OpCode == OpCodes.Call),
          "GetBoneCount was not forwarded to the adapter");
    var unsupported = cvrInput.Methods.Single(m => m.Name == "Unsupported");
    Check(unsupported.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 1),
          "unsupported CVRInput method did not return InvalidParam");
    var native = module.GetType("Valve.VR.NativeEntry")!.Methods.Single();
    Check(!native.IsPInvokeImpl && native.Body.Instructions.Any(i => i.OpCode == OpCodes.Throw),
          "legacy native import was not replaced with a managed failure");
    var interfaceGetter = module.GetType("Valve.VR.OpenVR")!.Methods.Single(m => m.Name == "get_CVRInput");
    Check(interfaceGetter.Body.Instructions.Any(i => i.OpCode == OpCodes.Call),
          "OpenVR interface getter was not redirected to the adapter");
    Check(cvrInput.Fields.Single(f => f.Name == "serializedState").IsPublic,
          "serialized field disappeared");

    using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "openxr-patch-manifest.json")));
    var result = report.RootElement;
    Check(result.GetProperty("forwardedMethods").GetInt32() == 1, "forward report mismatch");
    Check(result.GetProperty("blockedOpenVrNativeImports").GetInt32() == 1, "native import report mismatch");
    Check(result.GetProperty("serializedFieldsPreserved").GetBoolean(), "field preservation report failed");
    using var patchedGame = AssemblyDefinition.ReadAssembly(Path.Combine(output, "NativeH3VR.Game.dll"));
    Check(patchedGame.MainModule.AssemblyReferences.All(reference => reference.Name != patchedGame.Name.Name),
          "patch introduced a self-reference in the game assembly");
    var behaviour = patchedGame.MainModule.GetType("Valve.VR.SteamVR_Behaviour")!;
    var initialize = behaviour.Methods.Single(m => m.Name == "InitializeSteamVR");
    Check(initialize.Body.Instructions[0].OpCode == OpCodes.Ldc_I4_0 &&
          initialize.Body.Instructions[1].OpCode == OpCodes.Starg,
          "force-init argument was not normalized to preserve the configured OpenXR loader");
    var iterator = patchedGame.MainModule.Types.SelectMany(Flatten).Single(t =>
        t.FullName == "Valve.VR.SteamVR_Behaviour/<DoInitializeSteamVR>c__Iterator0");
    var moveNext = iterator.Methods.Single(m => m.Name == "MoveNext");
    Check(!moveNext.Body.Instructions.Any(i => i.Operand is MethodReference call &&
          call.DeclaringType.FullName == "UnityEngine.XR.XRSettings" && call.Name == "LoadDeviceByName") &&
          moveNext.Body.Instructions.Any(i => i.OpCode == OpCodes.Pop),
          "force-init iterator can still request the legacy OpenVR loader");
    Check(!patchedGame.MainModule.Types.SelectMany(Flatten).SelectMany(t => t.Methods)
          .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
          .Any(i => i.Operand is MethodReference call && call.DeclaringType.FullName == "UnityEngine.XR.XRSettings" && call.Name == "LoadDeviceByName"),
          "patched game contains a remaining XRSettings.LoadDeviceByName call");
    Check(result.GetProperty("legacyXrLoaderCallsRemoved").GetInt32() == 1,
          "legacy loader suppression count mismatch");
    Console.WriteLine("OpenXRPatch synthetic integration passed.");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "OpenXRPatch", "OpenXRPatch.csproj")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] CreateEmptyAssembly(string name)
{
    using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
    using var stream = new MemoryStream();
    assembly.Write(stream);
    return stream.ToArray();
}

static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes)
        foreach (var child in Flatten(nested)) yield return child;
}

static byte[] CreateGame()
{
    using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NativeH3VR.Game", new Version(1, 0)), "NativeH3VR.Game", ModuleKind.Dll);
    var module = assembly.MainModule;
    var steamVr = new TypeDefinition("Valve.VR", "SteamVR", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
    module.Types.Add(steamVr);
    var steamInitialize = new MethodDefinition("Initialize", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
    steamInitialize.Parameters.Add(new ParameterDefinition("force", ParameterAttributes.None, module.TypeSystem.Boolean));
    steamInitialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    steamVr.Methods.Add(steamInitialize);

    var behaviour = new TypeDefinition("Valve.VR", "SteamVR_Behaviour", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
    module.Types.Add(behaviour);
    var initialize = new MethodDefinition("InitializeSteamVR", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
    initialize.Parameters.Add(new ParameterDefinition("forceUnityVRMode", ParameterAttributes.None, module.TypeSystem.Boolean));
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
    var routine = new MethodDefinition("DoInitializeSteamVR", MethodAttributes.Private | MethodAttributes.HideBySig,
        new TypeReference("System.Collections", "IEnumerator", module, module.TypeSystem.CoreLibrary));
    routine.Parameters.Add(new ParameterDefinition("force", ParameterAttributes.None, module.TypeSystem.Boolean));
    routine.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
    routine.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    behaviour.Methods.Add(routine);
    var enableOpenVr = new MethodDefinition("EnableOpenVR", MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
    enableOpenVr.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    behaviour.Methods.Add(enableOpenVr);
    var start = Instruction.Create(OpCodes.Ldarg_0);
    var normal = Instruction.Create(OpCodes.Ldc_I4_0);
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, normal));
    initialize.Body.Instructions.Add(start);
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Call, routine));
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    initialize.Body.Instructions.Add(normal);
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Call, steamInitialize));
    initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    behaviour.Methods.Add(initialize);

    var iterator = new TypeDefinition("", "<DoInitializeSteamVR>c__Iterator0",
        TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class, module.TypeSystem.Object);
    behaviour.NestedTypes.Add(iterator);
    var moveNext = new MethodDefinition("MoveNext", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
    var xrSettings = new TypeReference("UnityEngine.XR", "XRSettings", module, module.TypeSystem.CoreLibrary);
    var loadDevice = new MethodReference("LoadDeviceByName", module.TypeSystem.Void, xrSettings) { HasThis = false };
    loadDevice.Parameters.Add(new ParameterDefinition("deviceName", ParameterAttributes.None, module.TypeSystem.String));
    moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "OpenVR"));
    moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Call, loadDevice));
    moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
    moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Call, enableOpenVr));
    moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    moveNext.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    iterator.Methods.Add(moveNext);
    using var stream = new MemoryStream();
    assembly.Write(stream);
    return stream.ToArray();
}

static byte[] CreateAdapter()
{
    using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NativeH3VR.OpenXR", new Version(1, 0)), "NativeH3VR.OpenXR", ModuleKind.Dll);
    var module = assembly.MainModule;
    var helper = new TypeDefinition("NativeH3VR", "NativeSteamVRInput", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
    module.Types.Add(helper);
    var error = new TypeReference("Valve.VR", "EVRInputError", module, module.TypeSystem.CoreLibrary);
    var getCount = new MethodDefinition("GetBoneCount", MethodAttributes.Public | MethodAttributes.Static, error);
    getCount.Parameters.Add(new ParameterDefinition("action", ParameterAttributes.None, module.TypeSystem.UInt64));
    getCount.Parameters.Add(new ParameterDefinition("count", ParameterAttributes.None, new ByReferenceType(module.TypeSystem.UInt32)));
    getCount.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    getCount.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    helper.Methods.Add(getCount);
    AddType(module, "NativeSteamVRSystem");
    var bootstrap = AddType(module, "NativeSteamVRBootstrap");
    var genericInterface = new MethodDefinition("Interface", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Object);
    genericInterface.GenericParameters.Add(new GenericParameter("T", genericInterface));
    genericInterface.ReturnType = genericInterface.GenericParameters[0];
    genericInterface.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
    genericInterface.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    bootstrap.Methods.Add(genericInterface);
    AddStatic(bootstrap, "Create", module.TypeSystem.Object, body: true);
    AddStatic(bootstrap, "EmptyRenderCoroutine", module.TypeSystem.Object, body: true);
    using var stream = new MemoryStream();
    assembly.Write(stream);
    return stream.ToArray();
}

static TypeDefinition AddType(ModuleDefinition module, string name)
{
    var type = new TypeDefinition("NativeH3VR", name, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
    module.Types.Add(type);
    return type;
}

static MethodDefinition AddStatic(TypeDefinition type, string name, TypeReference returnType, bool body)
{
    var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, returnType);
    if (body)
    {
        method.Body.Instructions.Add(returnType.MetadataType == MetadataType.Void ? Instruction.Create(OpCodes.Ret) : Instruction.Create(OpCodes.Ldnull));
        if (returnType.MetadataType != MetadataType.Void) method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    type.Methods.Add(method);
    return method;
}

static byte[] CreateFirstPass()
{
    using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NativeH3VR.FirstPass", new Version(1, 0)), "NativeH3VR.FirstPass", ModuleKind.Dll);
    var module = assembly.MainModule;
    var enumType = new TypeReference("System", "Enum", module, module.TypeSystem.CoreLibrary);
    var valve = new TypeDefinition("Valve.VR", "EVRInputError", TypeAttributes.Public | TypeAttributes.Sealed, enumType);
    var value__ = new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32);
    valve.Fields.Add(value__);
    foreach (var item in new[] { ("None", 0), ("InvalidParam", 1), ("NameNotFound", 2), ("WrongType", 3), ("InvalidHandle", 4), ("InvalidDevice", 5), ("NoData", 6), ("InvalidSkeleton", 7) })
    {
        var field = new FieldDefinition(item.Item1, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, valve);
        field.Constant = item.Item2;
        valve.Fields.Add(field);
    }
    module.Types.Add(valve);
    var cvr = new TypeDefinition("Valve.VR", "CVRInput", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
    module.Types.Add(cvr);
    cvr.Fields.Add(new FieldDefinition("serializedState", FieldAttributes.Public, module.TypeSystem.Int32));
    var count = new MethodDefinition("GetBoneCount", MethodAttributes.Public | MethodAttributes.HideBySig, valve);
    count.Parameters.Add(new ParameterDefinition("action", ParameterAttributes.None, module.TypeSystem.UInt64));
    count.Parameters.Add(new ParameterDefinition("count", ParameterAttributes.None, new ByReferenceType(module.TypeSystem.UInt32)));
    count.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    count.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    cvr.Methods.Add(count);
    var unsupported = new MethodDefinition("Unsupported", MethodAttributes.Public | MethodAttributes.HideBySig, valve);
    unsupported.Parameters.Add(new ParameterDefinition("action", ParameterAttributes.None, module.TypeSystem.UInt64));
    unsupported.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    unsupported.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    cvr.Methods.Add(unsupported);
    var openVr = new TypeDefinition("Valve.VR", "OpenVR", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
    module.Types.Add(openVr);
    var getter = new MethodDefinition("get_CVRInput", MethodAttributes.Public | MethodAttributes.Static, cvr);
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
    getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    openVr.Methods.Add(getter);
    var nativeType = new TypeDefinition("Valve.VR", "NativeEntry", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
    module.Types.Add(nativeType);
    var native = new MethodDefinition("Call", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PInvokeImpl, module.TypeSystem.Void);
    var nativeModule = new ModuleReference("openvr_api");
    module.ModuleReferences.Add(nativeModule);
    native.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "Call", nativeModule);
    nativeType.Methods.Add(native);
    var tracked = new TypeDefinition("Valve.VR", "ETrackedPropertyError", TypeAttributes.Public | TypeAttributes.Sealed, enumType);
    tracked.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
    var unknown = new FieldDefinition("TrackedProp_UnknownProperty", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, tracked);
    unknown.Constant = 1;
    tracked.Fields.Add(unknown);
    module.Types.Add(tracked);
    var compositor = new TypeDefinition("Valve.VR", "EVRCompositorError", TypeAttributes.Public | TypeAttributes.Sealed, enumType);
    compositor.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
    var request = new FieldDefinition("RequestFailed", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, compositor);
    request.Constant = 1;
    compositor.Fields.Add(request);
    module.Types.Add(compositor);
    using var stream = new MemoryStream();
    assembly.Write(stream);
    return stream.ToArray();
}
