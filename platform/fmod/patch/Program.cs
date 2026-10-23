using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length != 2)
    throw new ArgumentException(
        "Usage: Patch <Steam-patched FirstPass.dll> <output FirstPass.dll>");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory("audit-managed/ported");
resolver.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/PlaybackEngines/" +
                            "MacStandaloneSupport/Variations/mono/Managed");
using var assembly =
    AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
static IEnumerable<TypeDefinition> All(TypeDefinition type) {
    yield return type;
    foreach (var nested in type.NestedTypes)
        foreach (var item in All(nested))
            yield return item;
}
var module = assembly.MainModule;
var native = new ModuleReference("NativeH3VRFMOD");
module.ModuleReferences.Add(native);
int count = 0;
foreach (var method in module.Types.SelectMany(All)
             .SelectMany(t => t.Methods)
             .Where(m => m.IsPInvokeImpl && m.PInvokeInfo.Module.Name == "fmodstudio")) {
    method.PInvokeInfo.Module = native;
    method.PInvokeInfo.Attributes =
        (method.PInvokeInfo.Attributes & ~PInvokeAttributes.CallConvMask) |
        PInvokeAttributes.CallConvCdecl;
    count++;
}
var platform =
    module.GetType("FMODUnity.RuntimeUtils").Methods.Single(m => m.Name == "GetCurrentPlatform");
platform.Body = new MethodBody(platform);
var il = platform.Body.GetILProcessor();
il.Append(il.Create(OpCodes.Ldc_I4, 9));
il.Append(il.Create(OpCodes.Ret));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);
Console.WriteLine(
    $"Redirected {count} FMOD native imports to validated native ABI bridge: {args[1]}");
