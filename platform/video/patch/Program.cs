using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length < 3)
    throw new ArgumentException("Usage: Patch prepare|finish|normalize-self <inputGame.dll> " +
                                "<outputGame.dll> [backend.dll]");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/PlaybackEngines/" +
                            "MacStandaloneSupport/Variations/mono/Managed");
resolver.AddSearchDirectory("audit-managed/ported");
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
using var assembly =
    AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
if (args[0] == "normalize-self") {
    var ownName = module.Assembly.Name.Name;
    foreach (var reference in module.GetTypeReferences())
        if (reference.Scope is AssemblyNameReference scope && scope.Name == ownName)
            reference.Scope = module;
    foreach (var scope in module.AssemblyReferences.Where(scope => scope.Name == ownName).ToList())
        module.AssemblyReferences.Remove(scope);
} else if (args[0] == "prepare") {
    if (!assembly.CustomAttributes.Any(
            a => a.AttributeType.FullName ==
                     "System.Runtime.CompilerServices.InternalsVisibleToAttribute" &&
                 (string)a.ConstructorArguments[0].Value == "NativeH3VR.Platform")) {
        var attrType =
            new TypeReference("System.Runtime.CompilerServices", "InternalsVisibleToAttribute",
                              module, module.TypeSystem.CoreLibrary);
        var ctor =
            new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var attr = new CustomAttribute(ctor);
        attr.ConstructorArguments.Add(
            new CustomAttributeArgument(module.TypeSystem.String, "NativeH3VR.Platform"));
        assembly.CustomAttributes.Add(attr);
    }
} else if (args[0] == "finish") {
    using var backend = AssemblyDefinition.ReadAssembly(args[3]);
    var ctor = backend.MainModule.GetType("NativeH3VR.Platform.UnityMediaPlayer")
                   .Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
    var type = module.GetType("RenderHeads.Media.AVProVideo.MediaPlayer");
    var factory = type.Methods.Single(m => m.Name == "CreateMediaPlayer" && !m.IsStatic &&
                                           m.Parameters.Count == 0);
    factory.Body = new MethodBody(factory);
    var il = factory.Body.GetILProcessor();
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Newobj, module.ImportReference(ctor)));
    il.Append(il.Create(OpCodes.Ret));
    var quit = type.Methods.Single(m => m.Name == "OnApplicationQuit");
    foreach (var instruction in quit.Body.Instructions)
        if (instruction.Operand is MethodReference method && method.Name == "DeinitPlatform" &&
            (method.DeclaringType.Name == "WindowsMediaPlayer" ||
             method.DeclaringType.Name == "WindowsRtMediaPlayer")) {
            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }
    // The Windows factory used DOS 8.3 paths only to work around old Win32 limits.
    var helper = module.GetType("RenderHeads.Media.AVProVideo.Helper");
    var shortPath = helper.Methods.Single(m => m.Name == "ConvertLongPathToShortDOS83Path");
    shortPath.Body = new MethodBody(shortPath);
    il = shortPath.Body.GetILProcessor();
    il.Append(il.Create(OpCodes.Ldarg_0));
    il.Append(il.Create(OpCodes.Ret));
} else
    throw new ArgumentException("Unknown operation");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
assembly.Write(args[2]);
Console.WriteLine($"Video {args[0]}: {args[2]}");
