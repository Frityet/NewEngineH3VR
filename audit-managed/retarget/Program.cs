using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
var input = args.Length > 0? args[0]
    : System.IO.Path.Combine(
          System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
          "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/Managed");
var output = args.Length > 1? args[1] : "audit-managed/ported";
var engine = "/Applications/Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/" +
             "Variations/mono/Managed";
var shimPath = "audit-managed/shim/bin/Release/netstandard2.0/NativeH3VR.Compat.dll";
Directory.CreateDirectory(output);
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var nt in Types(n))
            yield return nt;
}
var resolver = new AliasResolver();
resolver.AddSearchDirectory(engine);
resolver.AddSearchDirectory(input);
resolver.AddSearchDirectory(Path.GetDirectoryName(shimPath));
var parameters = new ReaderParameters { AssemblyResolver = resolver };
var runtime = Directory.GetFiles(engine, "UnityEngine*.dll")
                  .Select(AssemblyDefinition.ReadAssembly)
                  .ToArray();
var types = runtime.SelectMany(a => a.MainModule.Types.SelectMany(Types))
                .GroupBy(t => t.FullName)
                .ToDictionary(g => g.Key, g => g.First());
var shim = AssemblyDefinition.ReadAssembly(shimPath);
var compatibility = shim.MainModule.GetType("NativeH3VR.Compatibility");
var renames =
    new Dictionary<string, string> { { "Assembly-CSharp", "NativeH3VR.Game" },
                                     { "Assembly-CSharp-firstpass", "NativeH3VR.FirstPass" },
                                     { "Assembly-UnityScript", "NativeH3VR.UnityScript" } };
var keep = new[] { "Assembly-CSharp",
                   "Assembly-CSharp-firstpass",
                   "Assembly-UnityScript",
                   "DinoFracture",
                   "ES2",
                   "MoodkieSecurity",
                   "Valve.Newtonsoft.Json",
                   "AmplifyBloom",
                   "AmplifyColor",
                   "UnityEngine.UI",
                   "UnityEngine.Networking" };
var changes = new List<object>();
var allOutput = new List<string>();
MethodDefinition New(string type, string name,
                     int count) => types[type].Methods.Single(m => m.Name == name &&
                                                                   m.Parameters.Count == count);
MethodDefinition Shim(string name) => compatibility.Methods.Single(m => m.Name == name);
bool IsOldEngine(TypeReference t) => t.Scope.Name is "UnityEngine" or "UnityEngine.dll" or
                                                     "UnityEngine.VR" or "UnityEngine.VR.dll";
foreach (var filename in keep) {
    using var a =
        AssemblyDefinition.ReadAssembly(Path.Combine(input, filename + ".dll"), parameters);
    var module = a.MainModule;
    var log = new List<object>();
    // Rewrite narrow method boundaries before mutating old declaring type metadata.
    foreach (var m in module.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasBody)) {
        foreach (var ins in m.Body.Instructions.ToArray()) {
            if (ins.Operand is MethodReference mr && IsOldEngine(mr.DeclaringType)) {
                MethodDefinition? replacement = null;
                string key = mr.DeclaringType.FullName + "::" + mr.Name;
                switch (key) {
                case "UnityEngine.Texture2D::EncodeToPNG":
                    replacement = Shim("EncodeToPNG");
                    break;
                case "UnityEngine.Texture2D::EncodeToEXR":
                    replacement = Shim("EncodeToEXR");
                    break;
                case "UnityEngine.Texture2D::LoadImage":
                    replacement = Shim("LoadImage");
                    break;
                case "UnityEngine.Networking.UnityWebRequest::get_isError":
                    replacement = Shim("IsWebRequestError");
                    break;
                case "UnityEngine.VR.VRSettings::get_renderScale":
                    replacement =
                        New("UnityEngine.XR.XRSettings", "get_eyeTextureResolutionScale", 0);
                    break;
                case "UnityEngine.VR.VRSettings::set_renderScale":
                    replacement =
                        New("UnityEngine.XR.XRSettings", "set_eyeTextureResolutionScale", 1);
                    break;
                case "UnityEngine.VR.VRDevice::get_model":
                    replacement = Shim("GetVRModel");
                    break;
                case "UnityEngine.WWW::get_audioClip":
                    replacement = New("UnityEngine.WWW", "GetAudioClip", 0);
                    break;
                case "UnityEngine.Camera::get_hdr":
                    replacement = New("UnityEngine.Camera", "get_allowHDR", 0);
                    break;
                case "UnityEngine.Graphics::DrawProceduralIndirect":
                    if (mr.Parameters.Count == 3)
                        replacement = types["UnityEngine.Graphics"].Methods.Single(
                            n => n.Name == "DrawProceduralIndirectNow" && n.Parameters.Count == 3 &&
                                 n.Parameters[1].ParameterType.Name == "ComputeBuffer");
                    break;
                case "UnityEngine.CanvasRenderer::AddUIVertexStream":
                    replacement = Shim("AddUIVertexStream");
                    break;
                case "UnityEngine.CanvasRenderer::SplitUIVertexStreams":
                    replacement = Shim("SplitUIVertexStreams");
                    break;
                case "UnityEngine.CanvasRenderer::CreateUIVertexStream":
                    replacement = Shim("CreateUIVertexStream");
                    break;
                case "UnityEngineInternal.ScriptingUtils::CreateDelegate":
                    replacement = Shim("CreateDelegate");
                    break;
                }
                if (replacement != null) {
                    log.Add(new { caller = m.FullName, old = mr.FullName,
                                  target = replacement.FullName });
                    ins.Operand = module.ImportReference(replacement);
                    if (replacement.IsStatic)
                        ins.OpCode = OpCodes.Call;
                }
            }
            if (ins.Operand is FieldReference field &&
                field.DeclaringType.FullName == "UnityEngine.UIVertex" &&
                field.Name.StartsWith("uv") && field.FieldType.FullName == "UnityEngine.Vector2") {
                var actual = types["UnityEngine.UIVertex"].Fields.Single(f => f.Name == field.Name);
                var imported = module.ImportReference(actual);
                var op = ins.OpCode;
                var il = m.Body.GetILProcessor();
                if (op == OpCodes.Ldfld) {
                    ins.Operand = imported;
                    var conv = types["UnityEngine.Vector4"].Methods.Single(
                        n => n.Name == "op_Implicit" &&
                             n.ReturnType.FullName == "UnityEngine.Vector2" &&
                             n.Parameters[0].ParameterType.FullName == "UnityEngine.Vector4");
                    il.InsertAfter(ins, il.Create(OpCodes.Call, module.ImportReference(conv)));
                } else if (op == OpCodes.Stfld) {
                    var conv = types["UnityEngine.Vector4"].Methods.Single(
                        n => n.Name == "op_Implicit" &&
                             n.ReturnType.FullName == "UnityEngine.Vector4" &&
                             n.Parameters[0].ParameterType.FullName == "UnityEngine.Vector2");
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = module.ImportReference(conv);
                    il.InsertAfter(ins, il.Create(OpCodes.Stfld, imported));
                } else
                    throw new Exception("Unsupported UIVertex field operation " + op + " in " +
                                        m.FullName);
                log.Add(new { caller = m.FullName, old = field.FullName, target = actual.FullName,
                              operation = op.Name });
            }
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s &&
                s.Contains("Assembly-CSharp")) {
                var result = s.Replace("Assembly-CSharp-firstpass", "NativeH3VR.FirstPass")
                                 .Replace("Assembly-CSharp", "NativeH3VR.Game");
                ins.Operand = result;
                log.Add(new { caller = m.FullName, old = s, target = result });
            }
        }
    }
    // Insertions must not leave short branches outside their signed-byte range.
    foreach (var method in module.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
            instruction.OpCode = instruction.OpCode.Code switch {
                Code.Br_S => OpCodes.Br,         Code.Brfalse_S => OpCodes.Brfalse,
                Code.Brtrue_S => OpCodes.Brtrue, Code.Beq_S => OpCodes.Beq,
                Code.Bge_S => OpCodes.Bge,       Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt,       Code.Bgt_Un_S => OpCodes.Bgt_Un,
                Code.Ble_S => OpCodes.Ble,       Code.Ble_Un_S => OpCodes.Ble_Un,
                Code.Blt_S => OpCodes.Blt,       Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave,
                _ => instruction.OpCode
            };
    foreach (var t in module.GetTypeReferences().Where(IsOldEngine).ToArray()) {
        var name = t.FullName.Replace("UnityEngine.VR.VR", "UnityEngine.XR.XR")
                       .Replace("UnityEngine.VR.", "UnityEngine.XR.")
                       .Replace("UnityEngine.Experimental.Rendering.RenderPipelineAsset",
                                "UnityEngine.Rendering.RenderPipelineAsset");
        if (name == "UnityEngineInternal.ScriptingUtils") {
            var target = module.ImportReference(compatibility);
            t.Namespace = target.Namespace;
            t.Name = target.Name;
            t.Scope = target.Scope;
            continue;
        }
        if (!types.TryGetValue(name, out var td))
            throw new Exception("Missing Unity type " + name + " from " + filename);
        var nt = module.ImportReference(td);
        if (t.FullName != nt.FullName)
            log.Add(new { old = t.FullName, target = nt.FullName });
        t.Namespace = nt.Namespace;
        t.Name = nt.Name;
        t.Scope = nt.Scope;
    }
    foreach (var ar in module.AssemblyReferences) {
        if (renames.TryGetValue(ar.Name, out var renamed))
            ar.Name = renamed;
    }
    if (renames.TryGetValue(a.Name.Name, out var own)) {
        a.Name.Name = own;
        module.Name = own + ".dll";
    }
    var outfile = Path.Combine(output, a.Name.Name + ".dll");
    a.Write(outfile);
    allOutput.Add(outfile);
    changes.Add(new { input = filename, output = Path.GetFileName(outfile), changes = log });
    Console.WriteLine($"{filename}: {log.Count} changes -> {outfile}");
}
File.Copy(shimPath, Path.Combine(output, "NativeH3VR.Compat.dll"), true);
File.WriteAllText(Path.Combine(output, "retarget-manifest.json"),
                  JsonSerializer.Serialize(changes,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Retargeting complete. P/Invoke implementations and VR backend still need " +
                  "native replacements. Original files unchanged.");

class AliasResolver : DefaultAssemblyResolver {
    public override AssemblyDefinition Resolve(AssemblyNameReference name,
                                               ReaderParameters parameters) {
        var original = name.Name switch { "NativeH3VR.Game" => "Assembly-CSharp",
                                          "NativeH3VR.FirstPass" => "Assembly-CSharp-firstpass",
                                          "NativeH3VR.UnityScript" => "Assembly-UnityScript",
                                          _ => null };
        return base.Resolve(original == null ? name
                                             : new AssemblyNameReference(original, name.Version),
                            parameters);
    }
}
