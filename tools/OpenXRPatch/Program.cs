using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if (args.Length != 3 && args.Length != 5)
{
    Console.Error.WriteLine("usage: OpenXRPatch <managed-input-dir> <NativeH3VR.OpenXR.dll> <patched-output-dir> [--unity-engine <Managed/UnityEngine-dir>]");
    return 64;
}

string input = Path.GetFullPath(args[0]);
string adapterPath = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
string? unityManaged = null;
if (args.Length == 5)
{
    if (args[3] != "--unity-engine") throw new ArgumentException("Expected --unity-engine <Managed/UnityEngine-dir>.");
    unityManaged = Path.GetFullPath(args[4]);
}
string[] assemblyNames = { "NativeH3VR.FirstPass.dll", "NativeH3VR.Game.dll" };
foreach (string name in assemblyNames)
    if (!File.Exists(Path.Combine(input, name)))
        throw new FileNotFoundException("Required migrated game assembly is missing.", Path.Combine(input, name));
if (!File.Exists(adapterPath)) throw new FileNotFoundException("OpenXR adapter is missing.", adapterPath);
Directory.CreateDirectory(output);
if (string.Equals(input.TrimEnd(Path.DirectorySeparatorChar),
                  output.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
    throw new InvalidOperationException("Input and output managed directories must be different.");
foreach (string file in Directory.GetFiles(input, "*", SearchOption.AllDirectories))
{
    string relative = Path.GetRelativePath(input, file);
    string destination = Path.Combine(output, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(file, destination, true);
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(input);
resolver.AddSearchDirectory(Path.GetDirectoryName(adapterPath)!);
unityManaged ??= Environment.GetEnvironmentVariable("NATIVEH3VR_UNITY_MANAGED") ??
                Environment.GetEnvironmentVariable("UnityEngineAssemblies");
if (!string.IsNullOrWhiteSpace(unityManaged)) resolver.AddSearchDirectory(Path.GetFullPath(unityManaged));
else if (!Directory.EnumerateFiles(input, "UnityEngine.CoreModule.dll", SearchOption.AllDirectories).Any())
    throw new InvalidOperationException("Set NATIVEH3VR_UNITY_MANAGED to Unity's Managed/UnityEngine directory so Cecil can preserve Unity type references.");
using var adapter = AssemblyDefinition.ReadAssembly(adapterPath,
    new ReaderParameters { AssemblyResolver = resolver });
var adapterNativeImports = AllTypes(adapter.MainModule).SelectMany(t => t.Methods)
    .Where(m => m.IsPInvokeImpl || m.PInvokeInfo != null).Select(m => m.FullName).ToArray();
if (adapterNativeImports.Length != 0)
    throw new InvalidOperationException("Managed OpenXR adapter must not contain native P/Invoke entries: " +
        string.Join("; ", adapterNativeImports));
var helpers = new Dictionary<string, TypeDefinition>
{
    ["CVRInput"] = RequiredType("NativeH3VR.NativeSteamVRInput"),
    ["CVRSystem"] = RequiredType("NativeH3VR.NativeSteamVRSystem"),
    ["CVRCompositor"] = RequiredType("NativeH3VR.NativeSteamVRSystem"),
    ["CVRChaperone"] = RequiredType("NativeH3VR.NativeSteamVRSystem"),
    ["CVRChaperoneSetup"] = RequiredType("NativeH3VR.NativeSteamVRSystem"),
};
var bootstrap = RequiredType("NativeH3VR.NativeSteamVRBootstrap");
var changes = new List<object>();
int blockedNativeImports = 0;
int forwardedMethods = 0;
int explicitUnsupported = 0;
int legacyXrLoaderCallsRemoved = 0;
var callersByCallee = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var callersByMethod = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var lifecycleRoots = new HashSet<string>(StringComparer.Ordinal);
var entryNames = new HashSet<string>(new[] { ".cctor", "Awake", "OnEnable", "Start",
    "FixedUpdate", "Update", "LateUpdate", "OnPreCull", "OnApplicationFocus",
    "CreateInstance", "Initialize", "InitializeInternal" }, StringComparer.Ordinal);

foreach (string name in assemblyNames)
{
    using var scan = AssemblyDefinition.ReadAssembly(Path.Combine(input, name));
    foreach (var type in AllTypes(scan.MainModule))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        {
            if (entryNames.Contains(method.Name)) lifecycleRoots.Add(method.FullName);
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference callee) continue;
                if (!callersByMethod.TryGetValue(callee.FullName, out var allCallers))
                {
                    allCallers = new SortedSet<string>(StringComparer.Ordinal);
                    callersByMethod.Add(callee.FullName, allCallers);
                }
                allCallers.Add(method.FullName);
                if (callee.DeclaringType.Namespace != "Valve.VR" ||
                    !callee.DeclaringType.Name.StartsWith("CVR", StringComparison.Ordinal)) continue;
                if (!callersByCallee.TryGetValue(callee.FullName, out var callers))
                {
                    callers = new SortedSet<string>(StringComparer.Ordinal);
                    callersByCallee.Add(callee.FullName, callers);
                }
                callers.Add(name + "!" + method.FullName);
            }
        }
}

string[] LifecyclePaths(IEnumerable<string> starts)
{
    var found = new SortedSet<string>(StringComparer.Ordinal);
    foreach (string start in starts)
    {
        var queue = new Queue<(string Method, string Path, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((start, start, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Method) || current.Depth > 12) continue;
            if (lifecycleRoots.Contains(current.Method))
            {
                found.Add(current.Path);
                continue;
            }
            if (!callersByMethod.TryGetValue(current.Method, out var parents)) continue;
            foreach (string parent in parents)
                queue.Enqueue((parent, current.Path + " <- " + parent, current.Depth + 1));
        }
    }
    return found.ToArray();
}

TypeDefinition RequiredType(string fullName) => adapter.MainModule.GetType(fullName)
    ?? throw new InvalidOperationException("Adapter type is missing: " + fullName);

bool Same(MethodDefinition target, MethodDefinition candidate) =>
    target.Name == candidate.Name && target.ReturnType.FullName == candidate.ReturnType.FullName &&
    target.Parameters.Select(p => p.ParameterType.FullName)
        .SequenceEqual(candidate.Parameters.Select(p => p.ParameterType.FullName));

int EnumValue(ModuleDefinition module, string enumName, string member)
{
    var type = module.GetType(enumName) ?? throw new InvalidOperationException("Missing enum " + enumName);
    var field = type.Fields.FirstOrDefault(f => f.Name == member && f.HasConstant)
        ?? throw new InvalidOperationException("Missing enum member " + enumName + "." + member);
    return Convert.ToInt32(field.Constant);
}

TypeReference ImportType(TypeReference type, ModuleDefinition target)
{
    if (type is ByReferenceType byReference)
        return new ByReferenceType(ImportType(byReference.ElementType, target));
    if (type is PointerType pointer)
        return new PointerType(ImportType(pointer.ElementType, target));
    if (type is ArrayType array)
    {
        var result = new ArrayType(ImportType(array.ElementType, target), array.Rank);
        for (int i = 0; i < array.Dimensions.Count; i++)
        {
            var dimension = array.Dimensions[i];
            result.Dimensions[i] = new ArrayDimension(dimension.LowerBound, dimension.UpperBound);
        }
        return result;
    }
    if (type is GenericInstanceType generic)
    {
        var result = new GenericInstanceType(ImportType(generic.ElementType, target));
        foreach (var argument in generic.GenericArguments)
            result.GenericArguments.Add(ImportType(argument, target));
        return result;
    }
    if (type is OptionalModifierType optional)
        return new OptionalModifierType(ImportType(optional.ModifierType, target), ImportType(optional.ElementType, target));
    if (type is RequiredModifierType required)
        return new RequiredModifierType(ImportType(required.ModifierType, target), ImportType(required.ElementType, target));
    if (type is PinnedType pinned)
        return new PinnedType(ImportType(pinned.ElementType, target));
    if (type is SentinelType sentinel)
        return new SentinelType(ImportType(sentinel.ElementType, target));
    if (type is GenericParameter)
        return type;

    // If a helper signature refers back to a type owned by the assembly being
    // rewritten, bind it to that module's local definition. Otherwise Cecil
    // writes a reference from an assembly to itself, which Unity rejects.
    if (type.Scope?.Name == target.Assembly.Name.Name)
    {
        var local = AllTypes(target).FirstOrDefault(candidate => candidate.FullName == type.FullName);
        if (local != null) return local;
    }
    return target.ImportReference(type);
}

MethodReference ImportMethod(MethodDefinition method, ModuleDefinition target)
{
    var imported = new MethodReference(method.Name, ImportType(method.ReturnType, target),
        ImportType(method.DeclaringType, target))
    {
        HasThis = method.HasThis,
        ExplicitThis = method.ExplicitThis,
        CallingConvention = method.CallingConvention
    };
    foreach (var generic in method.GenericParameters)
        imported.GenericParameters.Add(new GenericParameter(generic.Name, imported)
        {
            Attributes = generic.Attributes
        });
    foreach (var parameter in method.Parameters)
        imported.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
            ImportType(parameter.ParameterType, target)));
    return imported;
}

void Replace(MethodDefinition method, IEnumerable<Instruction> body)
{
    method.IsPInvokeImpl = false;
    method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
    foreach (var attribute in method.CustomAttributes.Where(a =>
                 a.AttributeType.FullName == "System.Runtime.InteropServices.DllImportAttribute").ToArray())
        method.CustomAttributes.Remove(attribute);
    method.Body = new MethodBody(method);
    var il = method.Body.GetILProcessor();
    foreach (var instruction in body) il.Append(instruction);
}

IEnumerable<Instruction> ForwardBody(MethodDefinition target, MethodDefinition helper,
                                     bool loadThis = false)
{
    if (loadThis) yield return Instruction.Create(OpCodes.Ldarg_0);
    foreach (var parameter in target.Parameters)
        yield return Instruction.Create(OpCodes.Ldarg, parameter);
    yield return Instruction.Create(OpCodes.Call, ImportMethod(helper, target.Module));
    yield return Instruction.Create(OpCodes.Ret);
}

IEnumerable<Instruction> FailureBody(MethodDefinition method, TypeDefinition owner)
{
    string returnName = method.ReturnType.Name;
    if (owner.Name == "CVRInput" && method.ReturnType.FullName == "Valve.VR.EVRInputError")
    {
        foreach (var parameter in method.Parameters.Where(p => p.ParameterType is ByReferenceType))
        {
            var element = ((ByReferenceType)parameter.ParameterType).ElementType;
            yield return Instruction.Create(OpCodes.Ldarg, parameter);
            yield return Instruction.Create(OpCodes.Initobj, ImportType(element, method.Module));
        }
        yield return Instruction.Create(OpCodes.Ldc_I4,
            EnumValue(method.Module, "Valve.VR.EVRInputError", "InvalidParam"));
        yield return Instruction.Create(OpCodes.Ret);
        yield break;
    }
    if (owner.Name == "CVRCompositor" && returnName == "EVRCompositorError")
    {
        yield return Instruction.Create(OpCodes.Ldc_I4,
            EnumValue(method.Module, "Valve.VR.EVRCompositorError", "RequestFailed"));
        yield return Instruction.Create(OpCodes.Ret);
        yield break;
    }
    if (owner.Name == "CVRSystem" && returnName == "ETrackedPropertyError")
    {
        int code = EnumValue(method.Module, "Valve.VR.ETrackedPropertyError", "TrackedProp_UnknownProperty");
        foreach (var p in method.Parameters.Where(p => p.ParameterType is ByReferenceType b && b.ElementType.Name == "ETrackedPropertyError"))
        {
            yield return Instruction.Create(OpCodes.Ldarg, p);
            yield return Instruction.Create(OpCodes.Ldc_I4, code);
            yield return Instruction.Create(OpCodes.Stind_I4);
        }
    }
    if (owner.Name == "CVRSystem" && method.Parameters.Any(p =>
            p.ParameterType is ByReferenceType b && b.ElementType.Name == "ETrackedPropertyError"))
    {
        foreach (var parameter in method.Parameters.Where(p => p.ParameterType is ByReferenceType))
        {
            var element = ((ByReferenceType)parameter.ParameterType).ElementType;
            yield return Instruction.Create(OpCodes.Ldarg, parameter);
            yield return Instruction.Create(OpCodes.Initobj, ImportType(element, method.Module));
        }
        foreach (var parameter in method.Parameters.Where(p =>
                     p.ParameterType is ByReferenceType b && b.ElementType.Name == "ETrackedPropertyError"))
        {
            yield return Instruction.Create(OpCodes.Ldarg, parameter);
            yield return Instruction.Create(OpCodes.Ldc_I4,
                EnumValue(method.Module, "Valve.VR.ETrackedPropertyError", "TrackedProp_UnknownProperty"));
            yield return Instruction.Create(OpCodes.Stind_I4);
        }
        if (method.ReturnType.MetadataType == MetadataType.Void)
        {
            yield return Instruction.Create(OpCodes.Ret);
        }
        else
        {
            var local = new VariableDefinition(ImportType(method.ReturnType, method.Module));
            method.Body.Variables.Add(local);
            method.Body.InitLocals = true;
            yield return Instruction.Create(OpCodes.Ldloca, local);
            yield return Instruction.Create(OpCodes.Initobj, local.VariableType);
            yield return Instruction.Create(OpCodes.Ldloc, local);
            yield return Instruction.Create(OpCodes.Ret);
        }
        yield break;
    }
    yield return Instruction.Create(OpCodes.Ldstr, "Unity XR adapter does not implement " + method.FullName);
    var exception = new TypeReference("System", "NotSupportedException", method.Module,
                                      method.Module.TypeSystem.CoreLibrary);
    var constructor = new MethodReference(".ctor", method.Module.TypeSystem.Void, exception) { HasThis = true };
    constructor.Parameters.Add(new ParameterDefinition(method.Module.TypeSystem.String));
    yield return Instruction.Create(OpCodes.Newobj, constructor);
    yield return Instruction.Create(OpCodes.Throw);
}

foreach (string name in assemblyNames)
{
    string source = Path.Combine(input, name);
    using var assembly = AssemblyDefinition.ReadAssembly(source,
        new ReaderParameters { ReadWrite = false, AssemblyResolver = resolver });
    var module = assembly.MainModule;

    // SteamVR's force-init path asks Unity to load the OpenVR XR device. That
    // would replace the project's configured OpenXR loader, so force mode is
    // normalized to the ordinary SteamVR.Initialize(false) path. Keep the
    // iterator guard too: callers can invoke DoInitializeSteamVR directly.
    if (name == "NativeH3VR.Game.dll")
        ProtectConfiguredOpenXrLoader(module, changes, ref legacyXrLoaderCallsRemoved);

    // Convert every legacy OpenVR P/Invoke into a managed failure. This also
    // blocks missed call paths in game code, rather than relying on the main
    // CVR wrapper classes to cover every imported entry point.
    foreach (var type in AllTypes(module))
        foreach (var method in type.Methods.Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null).ToArray())
        {
            string library = method.PInvokeInfo.Module.Name;
            if (!library.Contains("openvr", StringComparison.OrdinalIgnoreCase) &&
                !library.Contains("ValveIndexMacOS", StringComparison.OrdinalIgnoreCase)) continue;
            Replace(method, FailureBody(method, type));
            blockedNativeImports++;
            changes.Add(new { assembly = name, method = method.FullName, kind = "blocked-openvr-native-import", library });
        }

    foreach (var type in AllTypes(module).Where(t => t.Namespace == "Valve.VR" && t.Name.StartsWith("CVR", StringComparison.Ordinal)))
    {
        if (!helpers.TryGetValue(type.Name, out var helperType)) continue;
        foreach (var method in type.Methods.Where(m => m.IsPublic && !m.IsConstructor && !m.IsStatic && m.HasBody).ToArray())
        {
            MethodDefinition? helper = helperType.Methods.FirstOrDefault(m => m.IsStatic && Same(method, m));
            if (helper != null)
            {
                Replace(method, ForwardBody(method, helper, loadThis: false));
                forwardedMethods++;
                changes.Add(new { assembly = name, method = method.FullName, kind = "unity-xr-managed-adapter" });
            }
            else
            {
                Replace(method, FailureBody(method, type));
                explicitUnsupported++;
                string[] callers = callersByCallee.TryGetValue(method.FullName, out var callsites)
                    ? callsites.ToArray() : Array.Empty<string>();
                string[] lifecyclePaths = LifecyclePaths(callers.Select(c => c[(c.IndexOf('!') + 1)..]));
                changes.Add(new
                {
                    assembly = name,
                    method = method.FullName,
                    kind = "explicit-unsupported",
                    directCallers = callers,
                    lifecycleCallPaths = lifecyclePaths
                });
            }
        }
    }

    var openVr = AllTypes(module).FirstOrDefault(t => t.Namespace == "Valve.VR" && t.Name == "OpenVR");
    if (openVr != null)
    {
        foreach (var method in openVr.Methods.Where(m => m.IsPublic && m.IsStatic && m.HasBody).ToArray())
        {
            if (method.Name.StartsWith("get_", StringComparison.Ordinal) && method.ReturnType.Name.StartsWith("CVR", StringComparison.Ordinal))
            {
                var interfaceMethod = bootstrap.Methods.Single(m => m.Name == "Interface" && m.HasGenericParameters && m.Parameters.Count == 0);
                var generic = new GenericInstanceMethod(module.ImportReference(interfaceMethod));
                generic.GenericArguments.Add(ImportType(method.ReturnType, module));
                Replace(method, new[] { Instruction.Create(OpCodes.Call, generic), Instruction.Create(OpCodes.Ret) });
                changes.Add(new { assembly = name, method = method.FullName, kind = "managed-unity-xr-interface" });
            }
            else if (method.IsPInvokeImpl)
            {
                Replace(method, FailureBody(method, openVr));
                explicitUnsupported++;
                changes.Add(new { assembly = name, method = method.FullName, kind = "blocked-openvr-entry-point" });
            }
        }
    }

    var steamVr = AllTypes(module).FirstOrDefault(t => t.Namespace == "Valve.VR" && t.Name == "SteamVR");
    if (steamVr != null)
    {
        var create = steamVr.Methods.FirstOrDefault(m => m.Name == "CreateInstance" && m.HasBody);
        var createHelper = bootstrap.Methods.FirstOrDefault(m => m.Name == "Create" && m.IsStatic && m.Parameters.Count == 0);
        if (create != null && createHelper != null)
        {
            Replace(create, ForwardBody(create, createHelper));
            changes.Add(new { assembly = name, method = create.FullName, kind = "unity-xr-session-bootstrap" });
        }
    }

    // The SteamVR renderer performs its own per-eye camera renders and compositor
    // submissions. Unity XR owns both jobs, so stop those loops and leave the
    // scene cameras enabled for the active Unity XR display subsystem.
    var renderer = AllTypes(module).FirstOrDefault(t => t.Namespace == "Valve.VR" && t.Name == "SteamVR_Render");
    if (renderer != null)
    {
        foreach (var method in renderer.Methods.Where(m => m.HasBody &&
                     new[] { "RenderLoop", "Render", "OnPreCull" }.Contains(m.Name)).ToArray())
        {
            if (method.ReturnType.FullName == "System.Collections.IEnumerator")
            {
                var empty = bootstrap.Methods.Single(m => m.Name == "EmptyRenderCoroutine" && m.IsStatic);
                Replace(method, new[] { Instruction.Create(OpCodes.Call, module.ImportReference(empty)),
                                         Instruction.Create(OpCodes.Ret) });
            }
            else if (method.ReturnType.MetadataType == MetadataType.Void)
            {
                Replace(method, new[] { Instruction.Create(OpCodes.Ret) });
            }
            else
            {
                Replace(method, FailureBody(method, renderer));
            }
            changes.Add(new { assembly = name, method = method.FullName, kind = "unity-xr-owns-stereo-rendering" });
        }
    }

    string target = Path.Combine(output, name);
    assembly.Write(target);
}

foreach (string name in assemblyNames)
{
    using var patched = AssemblyDefinition.ReadAssembly(Path.Combine(output, name),
        new ReaderParameters { AssemblyResolver = resolver });
    var lingering = AllTypes(patched.MainModule).SelectMany(t => t.Methods)
        .Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null &&
            (m.PInvokeInfo.Module.Name.Contains("openvr", StringComparison.OrdinalIgnoreCase) ||
             m.PInvokeInfo.Module.Name.Contains("ValveIndexMacOS", StringComparison.OrdinalIgnoreCase)))
        .Select(m => m.FullName + " -> " + m.PInvokeInfo!.Module.Name).ToArray();
    var vendorAssemblyRefs = patched.MainModule.AssemblyReferences
        .Where(r => r.Name.Contains("ValveIndexMacOS", StringComparison.OrdinalIgnoreCase))
        .Select(r => r.Name).ToArray();
    var legacyLoaderCalls = AllTypes(patched.MainModule).Where(t => t.FullName.StartsWith("Valve.VR.", StringComparison.Ordinal))
        .SelectMany(t => t.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Where(i => IsOpenVrLoaderCall(i))
                .Select(_ => t.FullName + "::" + m.Name)))
        .ToArray();
    if (lingering.Length != 0 || vendorAssemblyRefs.Length != 0 || legacyLoaderCalls.Length != 0)
        throw new InvalidOperationException("OpenXR patch audit failed for " + name + ": " +
            string.Join("; ", lingering.Concat(vendorAssemblyRefs).Concat(legacyLoaderCalls)));
}

int serializedFieldCount = 0;
foreach (string name in assemblyNames)
{
    using var before = AssemblyDefinition.ReadAssembly(Path.Combine(input, name));
    using var after = AssemblyDefinition.ReadAssembly(Path.Combine(output, name));
    var beforeFields = SerializedFieldShape(before.MainModule);
    var afterFields = SerializedFieldShape(after.MainModule);
    if (!beforeFields.SequenceEqual(afterFields, StringComparer.Ordinal))
        throw new InvalidOperationException("OpenXR patch changed serialized Unity fields in " + name);
    serializedFieldCount += beforeFields.Length;
}

File.Copy(adapterPath, Path.Combine(output, Path.GetFileName(adapterPath)), true);
var result = new
{
    backend = "Unity XR / OpenXR provider",
    input,
    adapterPath,
    output,
    blockedOpenVrNativeImports = blockedNativeImports,
    legacyXrLoaderCallsRemoved,
    forwardedMethods,
    adapterNativeImports = adapterNativeImports.Length,
    explicitUnsupportedMethods = explicitUnsupported,
    serializedFieldsPreserved = true,
    serializedFieldCount,
    lifecycleRoots = lifecycleRoots.OrderBy(v => v).ToArray(),
    changes
};
File.WriteAllText(Path.Combine(output, "openxr-patch-manifest.json"),
    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new
{
    blockedOpenVrNativeImports = blockedNativeImports,
    legacyXrLoaderCallsRemoved,
    forwardedMethods,
    explicitUnsupportedMethods = explicitUnsupported
}));
return 0;

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
{
    foreach (var type in module.Types)
    {
        yield return type;
        foreach (var nested in Nested(type)) yield return nested;
    }
}
static IEnumerable<TypeDefinition> Nested(TypeDefinition type)
{
    foreach (var nested in type.NestedTypes)
    {
        yield return nested;
        foreach (var child in Nested(nested)) yield return child;
    }
}
static string[] SerializedFieldShape(ModuleDefinition module) => AllTypes(module)
    .SelectMany(t => t.Fields.Where(f => f.IsPublic || f.CustomAttributes.Any(a =>
        a.AttributeType.FullName == "UnityEngine.SerializeField" ||
        a.AttributeType.FullName == "UnityEngine.SerializeReference"))
        .Select(f => t.FullName + "|" + f.Name + "|" + f.FieldType.FullName + "|" +
            string.Join(",", f.CustomAttributes.Select(a => a.AttributeType.FullName).OrderBy(v => v, StringComparer.Ordinal))))
    .OrderBy(v => v, StringComparer.Ordinal).ToArray();

static bool IsOpenVrLoaderCall(Instruction instruction) =>
    instruction.Operand is MethodReference method &&
    method.DeclaringType.FullName == "UnityEngine.XR.XRSettings" &&
    method.Name == "LoadDeviceByName";

static void ProtectConfiguredOpenXrLoader(ModuleDefinition module, List<object> changes,
                                          ref int removedCalls)
{
    var behaviour = AllTypes(module).SingleOrDefault(t => t.Namespace == "Valve.VR" &&
        t.Name == "SteamVR_Behaviour")
        ?? throw new InvalidOperationException("Expected Valve.VR.SteamVR_Behaviour in NativeH3VR.Game.dll; refusing to leave a possible OpenVR loader switch unguarded.");
    var initialize = behaviour.Methods.SingleOrDefault(m => m.Name == "InitializeSteamVR" &&
        !m.IsStatic && m.ReturnType.MetadataType == MetadataType.Void && m.Parameters.Count == 1 &&
        m.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean && m.HasBody)
        ?? throw new InvalidOperationException("Expected SteamVR_Behaviour.InitializeSteamVR(bool) with a method body; refusing to leave a possible OpenVR loader switch unguarded.");
    bool callsForceCoroutine = initialize.Body.Instructions.Any(i => i.Operand is MethodReference called &&
        called.DeclaringType.FullName == behaviour.FullName && called.Name == "DoInitializeSteamVR" &&
        called.Parameters.Count == 1 && called.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
    bool callsNormalInitialize = initialize.Body.Instructions.Any(i => i.Operand is MethodReference called &&
        called.DeclaringType.FullName == "Valve.VR.SteamVR" && called.Name == "Initialize" &&
        called.Parameters.Count == 1 && called.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
    if (!callsForceCoroutine || !callsNormalInitialize ||
        !initialize.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldarg_1))
        throw new InvalidOperationException("SteamVR_Behaviour.InitializeSteamVR(bool) no longer has the expected force and normal initialization paths; refusing to rewrite an unknown method shape.");

    var il = initialize.Body.GetILProcessor();
    il.InsertBefore(initialize.Body.Instructions[0], Instruction.Create(OpCodes.Ldc_I4_0));
    il.InsertBefore(initialize.Body.Instructions[1], Instruction.Create(OpCodes.Starg, initialize.Parameters[0]));
    changes.Add(new
    {
        assembly = "NativeH3VR.Game.dll",
        method = initialize.FullName,
        kind = "preserve-configured-openxr-loader"
    });

    var loaderCalls = AllTypes(module).Where(t => t.FullName.StartsWith("Valve.VR.", StringComparison.Ordinal))
        .SelectMany(t => t.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions
            .Where(IsOpenVrLoaderCall).Select(i => (Type: t, Method: m, Call: i))))
        .ToArray();
    foreach (var call in loaderCalls)
    {
        bool expectedIterator = call.Type.FullName == "Valve.VR.SteamVR_Behaviour/<DoInitializeSteamVR>c__Iterator0" &&
            call.Method.Name == "MoveNext" && call.Method.ReturnType.MetadataType == MetadataType.Boolean;
        var previous = call.Call.Previous;
        if (!expectedIterator || previous == null || previous.OpCode != OpCodes.Ldstr ||
            !Equals(previous.Operand, "OpenVR") ||
            !call.Method.Body.Instructions.Any(i => i.Operand is MethodReference called &&
                called.DeclaringType.FullName == "Valve.VR.SteamVR_Behaviour" &&
                called.Name == "EnableOpenVR" && called.Parameters.Count == 0))
            throw new InvalidOperationException("Found an unexpected SteamVR XRSettings.LoadDeviceByName site at " +
                call.Method.FullName + "; refusing a loader-changing path or an iterator without the expected SteamVR initialization continuation.");
        // Preserve the stack effect (consume the requested device string) while
        // ensuring Unity is never asked to switch away from the configured loader.
        call.Call.OpCode = OpCodes.Pop;
        call.Call.Operand = null;
        removedCalls++;
        changes.Add(new
        {
            assembly = "NativeH3VR.Game.dll",
            method = call.Method.FullName,
            kind = "suppress-openvr-loader-switch"
        });
    }
    if (loaderCalls.Length != 1)
        throw new InvalidOperationException("Expected exactly one legacy OpenVR loader request in the SteamVR force-init iterator, found " + loaderCalls.Length + ".");
}
