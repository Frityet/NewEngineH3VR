using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length > 0 && args[0] == "--dump") {
    foreach (var file in args.Skip(1)) {
        using var a = AssemblyDefinition.ReadAssembly(file);
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) {
            yield return t;
            foreach (var n in t.NestedTypes.SelectMany(Walk))
                yield return n;
        }
        foreach (var t in a.MainModule.Types.SelectMany(Walk))
            foreach (var m in t.Methods.Where(
                         m => m.HasBody && (m.Name == "UpdateAllScopes" ||
                                            (t.FullName.Contains("NativeStereoRenderer/<Start>") &&
                                             (m.Name == "MoveNext" || m.Name.Contains("Finally") ||
                                              m.Name == "Dispose"))))) {
                Console.WriteLine("METHOD " + m.FullName);
                Console.WriteLine("BODYHASH " + ScopePairPatch.Fingerprint(m));
                foreach (var i in m.Body.Instructions)
                    Console.WriteLine(m.Body.Instructions.IndexOf(i) + " " + i.OpCode + " " +
                                      (i.Operand is Instruction branch
                                           ? "IL:" + m.Body.Instructions.IndexOf(branch)
                                           : i.Operand));
                foreach (var e in m.Body.ExceptionHandlers)
                    Console.WriteLine("EH " + e.HandlerType + " " +
                                      m.Body.Instructions.IndexOf(e.TryStart) + ".." +
                                      m.Body.Instructions.IndexOf(e.TryEnd) + " " +
                                      m.Body.Instructions.IndexOf(e.HandlerStart) + ".." +
                                      m.Body.Instructions.IndexOf(e.HandlerEnd));
            }
    }
    return;
}

bool verifyOnly = args.Length > 0 && args[0] == "--verify";
if (verifyOnly)
    args = args.Skip(1).ToArray();
if (args.Length < 4 || args.Length > 6)
    throw new ArgumentException("[--verify] input Game.dll, candidate XR.dll, output Game.dll, " +
                                "report.json, [dependency directory], [baseline XR.dll]");
var paths = args.Select(Path.GetFullPath).ToArray();
if (paths.Distinct().Count() != paths.Length)
    throw new ArgumentException("Input/helper/output/report must differ");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(paths[0]));
resolver.AddSearchDirectory(Path.GetDirectoryName(paths[1]));
if (paths.Length >= 5)
    resolver.AddSearchDirectory(paths[4]);
var options = new ReaderParameters { AssemblyResolver = resolver };
using var original = AssemblyDefinition.ReadAssembly(paths[0], options);
using var candidate = AssemblyDefinition.ReadAssembly(paths[0], options);
using var xr = AssemblyDefinition.ReadAssembly(paths[1], options);
var target = StereoPackingPatch.Apply(candidate, xr);
var scopeTarget = ScopePairPatch.Apply(candidate, xr);
Directory.CreateDirectory(Path.GetDirectoryName(paths[2])!);
var temporary = verifyOnly ? paths[2] : paths[2] + ".verifying-" + Guid.NewGuid().ToString("N");
try {
    if (!verifyOnly)
        candidate.Write(temporary);
    using var verified = AssemblyDefinition.ReadAssembly(temporary, options);
    int checks = 0;
    void Require(bool test, string message) {
        checks++;
        if (!test)
            throw new InvalidOperationException(message);
    }
    string Hash(string p) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
    IEnumerable<TypeDefinition> Types(TypeDefinition t) {
        yield return t;
        foreach (var n in t.NestedTypes.SelectMany(Types))
            yield return n;
    }
    string Attrs(IEnumerable<CustomAttribute> attrs) => string.Join(
        "|", attrs.Select(a => a.Constructor.FullName + ":" + Convert.ToHexString(a.GetBlob())));
    string Security(IEnumerable<SecurityDeclaration> values) =>
        string.Join("|", values.Select(s => s.Action + ":" + Convert.ToHexString(s.GetBlob())));
string Constant(object? value)=>value switch{float f=>"f:"+BitConverter.SingleToInt32Bits(f),double d=>"d:"+BitConverter.DoubleToInt64Bits(d),byte[] bytes=>Convert.ToHexString(bytes),_=>value?.ToString()??"<null>"};
string Marshal(IMarshalInfoProvider provider) =>
    !provider.HasMarshalInfo
        ? "none"
        : provider.MarshalInfo.GetType().Name + ":" +
              string.Join(
                  "|", provider.MarshalInfo.GetType()
                           .GetProperties()
                           .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                           .OrderBy(p => p.Name)
                           .Select(p => p.Name + "=" + Constant(p.GetValue(provider.MarshalInfo))));
string Param(ParameterDefinition p) => p.Name + ":" + p.Attributes + ":" +
                                       p.ParameterType.FullName + ":" + p.HasConstant + ":" +
                                       (p.HasConstant ? Constant(p.Constant) : "") + ":" +
                                       Attrs(p.CustomAttributes) + ":" + Marshal(p);
string MethodMeta(MethodDefinition m) => JsonSerializer.Serialize(new {
    m.FullName, m.Attributes, m.ImplAttributes, m.CallingConvention, m.HasThis, m.ExplicitThis,
    parameters = m.Parameters.Select(Param), returnAttributes = m.MethodReturnType.Attributes,
    returnCustomAttributes = Attrs(m.MethodReturnType.CustomAttributes),
    returnMarshal = Marshal(m.MethodReturnType), m.MethodReturnType.HasConstant,
    returnConstant = m.MethodReturnType.HasConstant ? Constant(m.MethodReturnType.Constant) : null,
    custom = Attrs(m.CustomAttributes), security = Security(m.SecurityDeclarations),
    overrides = m.Overrides.Select(o => o.FullName),
    generics = m.GenericParameters.Select(
        g => new { g.Name, g.Attributes,
                   constraints = g.Constraints.Select(c => c.ConstraintType.FullName),
                   custom = Attrs(g.CustomAttributes) }),
    pinvoke = m.HasPInvokeInfo ? m.PInvokeInfo.Module.Name + ":" + m.PInvokeInfo.EntryPoint + ":" +
                                     m.PInvokeInfo.Attributes
                               : null
});
string FieldMeta(FieldDefinition f) =>
    JsonSerializer.Serialize(new { f.FullName, f.Attributes, f.Offset, f.HasConstant,
                                   constant = f.HasConstant ? Constant(f.Constant) : null,
                                   initial = Convert.ToHexString(f.InitialValue),
                                   custom = Attrs(f.CustomAttributes), marshal = Marshal(f) });
string TypeMeta(TypeDefinition t, bool omitScopeProperty = false) => JsonSerializer.Serialize(
    new { t.FullName, t.Attributes, t.PackingSize, t.ClassSize, baseType = t.BaseType?.FullName,
          interfaces =
              t.Interfaces.Select(i => i.InterfaceType.FullName + ":" + Attrs(i.CustomAttributes)),
          custom = Attrs(t.CustomAttributes), security = Security(t.SecurityDeclarations),
          generics = t.GenericParameters.Select(
              g => new { g.Name, g.Attributes,
                         constraints = g.Constraints.Select(c => c.ConstraintType.FullName),
                         custom = Attrs(g.CustomAttributes) }),
          properties =
              t.Properties.Where(p => !omitScopeProperty || p.Name != "IsRenderingEye")
                  .Select(p => new { p.FullName, p.Attributes, get = p.GetMethod?.FullName,
                                     set = p.SetMethod?.FullName,
                                     other = p.OtherMethods.Select(m => m.FullName),
                                     parameters = p.Parameters.Select(Param), p.HasConstant,
                                     constant = p.HasConstant ? Constant(p.Constant) : null,
                                     custom = Attrs(p.CustomAttributes) }),
          events = t.Events.Select(e => new { e.FullName, e.Attributes, add = e.AddMethod?.FullName,
                                              remove = e.RemoveMethod?.FullName,
                                              invoke = e.InvokeMethod?.FullName,
                                              other = e.OtherMethods.Select(m => m.FullName),
                                              custom = Attrs(e.CustomAttributes) }) });
string Operand(object? operand,MethodDefinition m)=>operand switch{
    Instruction i=>"IL:"+m.Body.Instructions.IndexOf(i),Instruction[] values=>string.Join(",",values.Select(i=>"IL:"+m.Body.Instructions.IndexOf(i))),
    VariableDefinition v=>"local:"+v.Index,ParameterDefinition p=>"arg:"+p.Index,
    MemberReference member=>member.FullName+"@"+(member.DeclaringType?.Scope?.Name??(member as TypeReference)?.Scope?.Name),_=>Constant(operand)};
string Body(MethodDefinition m) =>
    !m.HasBody
        ? "none"
        : JsonSerializer.Serialize(
              new { m.Body.InitLocals, m.Body.MaxStackSize,
                    locals = m.Body.Variables.Select(v => v.VariableType.FullName),
                    code = m.Body.Instructions.Select(i => i.OpCode + ":" + Operand(i.Operand, m)),
                    eh = m.Body.ExceptionHandlers.Select(
                        e => e.HandlerType + ":" + e.CatchType + ":" + Operand(e.TryStart, m) +
                             ":" + Operand(e.TryEnd, m) + ":" + Operand(e.HandlerStart, m) + ":" +
                             Operand(e.HandlerEnd, m) + ":" + Operand(e.FilterStart, m)) });
Require(original.Name.FullName == verified.Name.FullName, "Assembly identity changed");
Require(original.MainModule.Mvid == verified.MainModule.Mvid, "Module MVID changed");
Require(original.MainModule.Attributes == verified.MainModule.Attributes &&
            original.MainModule.Architecture == verified.MainModule.Architecture &&
            original.MainModule.RuntimeVersion == verified.MainModule.RuntimeVersion,
        "Module flags changed");
Require(Attrs(original.CustomAttributes) == Attrs(verified.CustomAttributes) &&
            Security(original.SecurityDeclarations) == Security(verified.SecurityDeclarations),
        "Assembly metadata changed");
Require(Attrs(original.MainModule.CustomAttributes) == Attrs(verified.MainModule.CustomAttributes),
        "Module custom metadata changed");
string ResourceKey(Resource r) =>
    r.Name + ":" + r.Attributes + ":" +
    (r switch { EmbeddedResource e => Convert.ToHexString(SHA256.HashData(e.GetResourceData())),
                LinkedResource l => l.File + ":" + Convert.ToHexString(l.Hash),
                AssemblyLinkedResource a => a.Assembly.FullName,
                _ => throw new InvalidOperationException("Unknown resource kind") });
Require(original.MainModule.Resources.Select(ResourceKey)
            .SequenceEqual(verified.MainModule.Resources.Select(ResourceKey)),
        "Assembly resources changed");
Require(
    original.MainModule.ExportedTypes.Select(t => t.FullName + ":" + t.Attributes + ":" + t.Scope)
        .SequenceEqual(verified.MainModule.ExportedTypes.Select(t => t.FullName + ":" +
                                                                     t.Attributes + ":" + t.Scope)),
    "Exported type metadata changed");
Require(original.MainModule.AssemblyReferences.Select(a => a.FullName)
            .SequenceEqual(verified.MainModule.AssemblyReferences.Select(a => a.FullName)),
        "Assembly references changed");
Require(original.MainModule.ModuleReferences.Select(m => m.Name)
            .SequenceEqual(verified.MainModule.ModuleReferences.Select(m => m.Name)),
        "Native module references changed");
var beforeTypes = original.MainModule.Types.SelectMany(Types).ToArray();
var afterTypes = verified.MainModule.Types.SelectMany(Types).ToArray();
Require(beforeTypes.Length == afterTypes.Length, "Type count changed");
int untouchedBodies = 0;
for (int t = 0; t < beforeTypes.Length; t++) {
    var a = beforeTypes[t];
    var b = afterTypes[t];
    Require(TypeMeta(a) == TypeMeta(b), "Type/property/event metadata changed: " + a.FullName);
    Require(a.Fields.Select(FieldMeta).SequenceEqual(b.Fields.Select(FieldMeta)),
            "Field layout/data changed: " + a.FullName);
    Require(a.Methods.Count == b.Methods.Count, "Method count changed: " + a.FullName);
    for (int m = 0; m < a.Methods.Count; m++) {
        var old = a.Methods[m];
        var current = b.Methods[m];
        Require(MethodMeta(old) == MethodMeta(current), "Method metadata changed: " + old.FullName);
        if (old.FullName == target.FullName) {
            var il = current.Body.Instructions;
            Require(il.Count == 2 && il[0].OpCode == OpCodes.Call && il[1].OpCode == OpCodes.Ret,
                    "Candidate getter layout changed");
            var call = (MethodReference)il[0].Operand;
            Require(
                call.FullName ==
                        "System.Boolean NativeH3VR.NewEnginePostProcessing::get_IsPackedStereo()" &&
                    call.DeclaringType.Scope.Name == "NativeH3VR.XR.Managed",
                "Candidate getter targets wrong helper");
        } else if (old.FullName == scopeTarget.FullName) {
            Require(Body(scopeTarget) == Body(current),
                    "Scope setter replacements differ from exact expected body");
        } else {
            Require(Body(old) == Body(current), "Untouched method body changed: " + old.FullName);
            if (old.HasBody)
                untouchedBodies++;
        }
    }
}
var oldXrPath = paths.Length == 6
                    ? paths[5]
                    : Path.Combine(Path.GetDirectoryName(paths[0])!, "NativeH3VR.XR.Managed.dll");
using var oldXr = AssemblyDefinition.ReadAssembly(oldXrPath, options);
Require(oldXr.Name.FullName == xr.Name.FullName, "XR assembly identity changed");
Require(oldXr.MainModule.Attributes == xr.MainModule.Attributes &&
            oldXr.MainModule.Architecture == xr.MainModule.Architecture,
        "XR module flags changed");
Require(Attrs(oldXr.CustomAttributes) == Attrs(xr.CustomAttributes),
        "XR assembly attributes changed");
Require(oldXr.MainModule.AssemblyReferences.Select(a => a.FullName)
            .SequenceEqual(xr.MainModule.AssemblyReferences.Select(a => a.FullName)),
        "XR dependency identities changed");
var oldXrTypes = oldXr.MainModule.Types.SelectMany(Types).ToArray();
var xrTypes = xr.MainModule.Types.SelectMany(Types).ToDictionary(t => t.FullName);
Require(xrTypes.Count == oldXrTypes.Length + 2 &&
            xrTypes.ContainsKey(StereoPackingPatch.HelperType) &&
            xrTypes.ContainsKey(ScopePairPatch.HelperType),
        "Unexpected added/removed XR types");
int xrBodies = 0;
foreach (var before in oldXrTypes) {
    Require(xrTypes.TryGetValue(before.FullName, out var after),
            "XR type removed: " + before.FullName);
    bool scope = before.FullName == "NativeH3VR.NativeEyeProjection";
    Require(TypeMeta(before, scope) == TypeMeta(after!, scope),
            "XR type/property/event metadata changed: " + before.FullName);
    Require(before.Fields.Select(FieldMeta).SequenceEqual(after!.Fields.Select(FieldMeta)),
            "XR field layout/data changed: " + before.FullName);
    Require(after.Methods.Count == before.Methods.Count + (scope ? 1 : 0),
            "Unexpected XR methods: " + before.FullName);
    foreach (var method in before.Methods) {
        var current = after.Methods.SingleOrDefault(m => m.FullName == method.FullName);
        if (current == null)
            throw new InvalidOperationException("XR method removed: " + method.FullName);
        Require(MethodMeta(method) == MethodMeta(current),
                "XR method metadata changed: " + method.FullName);
        if (method.FullName ==
            "System.Boolean NativeH3VR.NativeStereoRenderer/<Start>c__Iterator0::MoveNext()") {
            Require(ScopePairPatch.Fingerprint(method) ==
                        "bfebe9be4c1e6b1f8c0cf8987428e8f35b2e0bafa3163811342a45efaec43dee",
                    "XR baseline coroutine changed");
            Require(ScopePairPatch.Fingerprint(current!) ==
                        "b1d6360f92735f26d59e51b6f819fac595c065fe3ed9bb38e6b281666189db99",
                    "XR pair-hook coroutine changed");
            int Map(int i) => i < 180 ? i : i < 337 ? i + 1 : i + 4;
   string MapOperand(object? operand)=>operand switch{Instruction branch=>"IL:"+Map(method.Body.Instructions.IndexOf(branch)),Instruction[] branches=>string.Join(",",branches.Select(i=>"IL:"+Map(method.Body.Instructions.IndexOf(i)))),_=>Operand(operand,method)};
   Require(method.Body.Instructions.Count == 433 && current!.Body.Instructions.Count == 437,
           "XR iterator instruction delta changed");
   Require(method.Body.InitLocals == current.Body.InitLocals &&
               method.Body.MaxStackSize == current.Body.MaxStackSize &&
               method.Body.Variables.Select(v => v.VariableType.FullName)
                   .SequenceEqual(current.Body.Variables.Select(v => v.VariableType.FullName)),
           "XR iterator locals/stack changed");
   for (int i = 0; i < 433; i++) {
       var oldIL = method.Body.Instructions[i];
       var newIL = current.Body.Instructions[Map(i)];
       Require(oldIL.OpCode == newIL.OpCode &&
                   MapOperand(oldIL.Operand) == Operand(newIL.Operand, current),
               "Existing XR iterator instruction changed: " + i);
   }
   bool CallAt(int i, string name) =>
       current.Body.Instructions[i].OpCode == OpCodes.Call &&
       ((MethodReference)current.Body.Instructions[i].Operand).FullName ==
           "System.Void NativeH3VR.NewEngineScopeActivation::" + name + "()";
   Require(CallAt(180, "BeginPair") && CallAt(339, "EndPair") &&
               current.Body.Instructions[338].OpCode == OpCodes.Leave &&
               current.Body.Instructions.IndexOf(
                   (Instruction)current.Body.Instructions[338].Operand) == 341 &&
               current.Body.Instructions[340].OpCode == OpCodes.Endfinally,
           "XR pair hook instructions changed");
   Require(method.Body.ExceptionHandlers.Count == 1 && current.Body.ExceptionHandlers.Count == 2,
           "XR finally count delta changed");
   var bounded = current.Body.ExceptionHandlers[0];
   var outer = current.Body.ExceptionHandlers[1];
   Require(bounded.HandlerType == ExceptionHandlerType.Finally &&
               current.Body.Instructions.IndexOf(bounded.TryStart) == 181 &&
               current.Body.Instructions.IndexOf(bounded.TryEnd) == 339 &&
               current.Body.Instructions.IndexOf(bounded.HandlerStart) == 339 &&
               current.Body.Instructions.IndexOf(bounded.HandlerEnd) == 341,
           "XR pair finally bounds changed");
   Require(outer.HandlerType == ExceptionHandlerType.Catch &&
               outer.CatchType.FullName == method.Body.ExceptionHandlers[0].CatchType.FullName &&
               current.Body.Instructions.IndexOf(outer.TryStart) == 180 &&
               current.Body.Instructions.IndexOf(outer.TryEnd) == 400 &&
               current.Body.Instructions.IndexOf(outer.HandlerStart) == 400 &&
               current.Body.Instructions.IndexOf(outer.HandlerEnd) == 405,
           "XR outer cleanup changed");
        } else {
            Require(Body(method) == Body(current!),
                    "XR existing method body changed: " + method.FullName);
            if (method.HasBody)
                xrBodies++;
        }
    }
}
var scopeGetter =
    xrTypes["NativeH3VR.NativeEyeProjection"].Methods.Single(m => m.Name == "get_IsRenderingEye");
Require(scopeGetter.IsPublic && scopeGetter.IsStatic &&
            scopeGetter.ReturnType.FullName == "System.Boolean" &&
            scopeGetter.Parameters.Count == 0,
        "XR eye scope accessor signature changed");
var scopeInstructions = scopeGetter.Body.Instructions;
Require(scopeInstructions.Count == 4 && scopeInstructions[0].OpCode == OpCodes.Ldsfld &&
            ((FieldReference)scopeInstructions[0].Operand).Name == "eyeCamera" &&
            scopeInstructions[1].OpCode == OpCodes.Ldnull &&
            scopeInstructions[2].OpCode == OpCodes.Call &&
            ((MethodReference)scopeInstructions[2].Operand).FullName ==
                "System.Boolean " +
                    "UnityEngine.Object::op_Inequality(UnityEngine.Object,UnityEngine.Object)" &&
            scopeInstructions[3].OpCode == OpCodes.Ret,
        "XR scope accessor must read existing eyeCamera only");
var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var sourceFiles = new[] { "native/managed/NativeH3VRXR.cs",
                          "native/managed/NativePostProcessStereo.cs",
                          "native/managed/NativeScopeActivation.cs",
                          "native/patch/StereoPackingPatch.cs",
                          "native/patch/ScopePairPatch.cs",
                          "native/patch/Program.cs",
                          "native/scope-pair-patch/Program.cs",
                          "native/scope-pair-patch/ScopePairPatch.csproj",
                          "native/build_managed.py",
                          "script/compose_managed.py",
                          "native/prepare_scope_pair.py" };
var proof = new {
    schema_version = 1,
    kind = "newengine-packed-stereo-scope-pair",
    verification_mode = verifyOnly ? "existing-candidate" : "prepared-candidate",
    engine_define = "NEWENGINE_UNITY_2021",
    sources = sourceFiles.ToDictionary(p => p, p => Hash(Path.Combine(sourceRoot, p))),
    scope = "Only packed-stereo getter and exact three scope activation setter calls change; " +
            "shared OptiH3VR CPU code untouched",
    input = paths[0],
    input_sha256 = Hash(paths[0]),
    output = paths[2],
    output_sha256 = Hash(temporary),
    helper = paths[1],
    helper_sha256 = Hash(paths[1]),
    target = target.FullName,
    old_policy = "constant true",
    new_policy = "false inside native-eye Begin/End, true outside; " +
                 "NEWENGINEH3VR_PACKED_STEREO_FIX=0 restores constant true",
    checks,
    untouched_method_bodies = untouchedBodies,
    changed_game_methods = new[] { target.FullName, scopeTarget.FullName },
    scope_setter_callsites = ScopePairPatch.Sites.Select(
        i => new { instruction_index = i, old_opcode = "callvirt",
                   old_method = ScopePairPatch.OldSetter, new_opcode = "call",
                   new_method = ScopePairPatch.Helper }),
    scope_setter_original_body_sha256 = ScopePairPatch.ExpectedBodyHash,
    changed_existing_xr_methods =
        new[] { "System.Boolean NativeH3VR.NativeStereoRenderer/<Start>c__Iterator0::MoveNext()" },
    xr_changed_body_fingerprints = new[] { new {
        method = "System.Boolean NativeH3VR.NativeStereoRenderer/<Start>c__Iterator0::MoveNext()",
        before_sha256 = "bfebe9be4c1e6b1f8c0cf8987428e8f35b2e0bafa3163811342a45efaec43dee",
        after_sha256 = "b1d6360f92735f26d59e51b6f819fac595c065fe3ed9bb38e6b281666189db99"
    } },
    xr_iterator_instruction_delta = 4,
    xr_iterator_finally_delta = 1,
    xr_added_types =
        new[] { "NativeH3VR.NewEnginePostProcessing", "NativeH3VR.NewEngineScopeActivation" },
    xr_added_methods =
        new[] { "System.Boolean NativeH3VR.NativeEyeProjection::get_IsRenderingEye()" },
    xr_added_properties = new[] { "NativeH3VR.NativeEyeProjection.IsRenderingEye" },
    helper_method = "System.Boolean NativeH3VR.NewEnginePostProcessing::get_IsPackedStereo()",
    expected_old_opcodes = new[] { "ldc.i4.1", "ret" },
    expected_new_opcodes = new[] { "call", "ret" },
    checks_include =
        new[] { "assembly identity/references/module flags",
                "all existing type/property/event metadata",
                "all existing field metadata/layout/initial data", "all existing method metadata",
                "every non-target method body, locals and exception handlers",
                "exact target call to helper getter",
                "XR existing types/fields/methods and all existing method bodies",
                "XR eye scope accessor reads existing eyeCamera only" },
    baseline_xr = oldXrPath,
    baseline_xr_sha256 = Hash(oldXrPath),
    xr_existing_method_bodies_preserved = xrBodies,
    xr_all_existing_type_and_field_layouts_preserved = true,
    all_existing_types_fields_properties_events_and_method_metadata_preserved = true,
    all_other_method_bodies_preserved = true
};
if (!verifyOnly)
    File.Move(temporary, paths[2], true);
File.WriteAllText(
    paths[3],
    JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine("PASS " + checks + " assertions; " + untouchedBodies +
                  " untouched method bodies; candidate only");
} finally {
    if (!verifyOnly && File.Exists(temporary))
        File.Delete(temporary);
}
