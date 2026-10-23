using System.Text.Json;
using Mono.Cecil;

if (args.Length != 2) {
    Console.Error.WriteLine("usage: ManagedExtensionCheck <old.dll> <new.dll>");
    return 2;
}

var failures = new List<string>();
var oldTypes = new List<string>();
var skippedTypes = new List<string>();
try {
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
    resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    var readerParameters =
        new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
    using var oldAssembly =
        AssemblyDefinition.ReadAssembly(Path.GetFullPath(args[0]), readerParameters);
    using var newAssembly =
        AssemblyDefinition.ReadAssembly(Path.GetFullPath(args[1]), readerParameters);
    var oldName = oldAssembly.Name;
    var newName = newAssembly.Name;
    if (AssemblyIdentity(oldName) != AssemblyIdentity(newName))
        failures.Add("assembly identity changed: " + AssemblyIdentity(oldName) + " -> " +
                     AssemblyIdentity(newName));

    var newReferences =
        newAssembly.MainModule.AssemblyReferences
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    foreach (var oldReference in oldAssembly.MainModule.AssemblyReferences) {
        if (!newReferences.TryGetValue(oldReference.Name, out var candidates))
            failures.Add("required assembly reference removed: " + AssemblyIdentity(oldReference));
        else if (!candidates.Any(newReference => AssemblyIdentity(oldReference) ==
                                                 AssemblyIdentity(newReference)))
            failures.Add("required assembly reference identity changed: " +
                         AssemblyIdentity(oldReference));
    }

    var newTypeMap =
        AllTypes(newAssembly.MainModule).ToDictionary(t => t.FullName, StringComparer.Ordinal);
    foreach (var oldType in AllTypes(oldAssembly.MainModule)) {
        if (IsCompilerGenerated(oldType)) {
            skippedTypes.Add(oldType.FullName);
            continue;
        }
        oldTypes.Add(oldType.FullName);
        if (!newTypeMap.TryGetValue(oldType.FullName, out var newType)) {
            failures.Add("type removed: " + oldType.FullName);
            continue;
        }
        CompareType(oldType, newType, failures);
    }

    var report =
        new { passed = failures.Count == 0,
              result = failures.Count == 0 ? "pass" : "fail",
              oldAssembly = AssemblyIdentity(oldName),
              newAssembly = AssemblyIdentity(newName),
              retainedOldTypeCount = oldTypes.Count,
              skippedCompilerGeneratedTypeCount = skippedTypes.Count,
              checkedReferences =
                  oldAssembly.MainModule.AssemblyReferences.Select(AssemblyIdentity).ToArray(),
              failures };
    Console.WriteLine(
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return failures.Count == 0 ? 0 : 1;
} catch (Exception ex) {
    Console.WriteLine(JsonSerializer.Serialize(
        new { passed = false, result = "error", error = ex.ToString(), failures },
        new JsonSerializerOptions { WriteIndented = true }));
    return 2;
}

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) {
    foreach (var type in module.Types)
        foreach (var nested in AllNestedTypes(type))
            yield return nested;
}

static IEnumerable<TypeDefinition> AllNestedTypes(TypeDefinition type) {
    yield return type;
    foreach (var nestedType in type.NestedTypes)
        foreach (var nested in AllNestedTypes(nestedType))
            yield return nested;
}

static bool IsCompilerGenerated(TypeDefinition type) {
    if (type.Name.StartsWith("<", StringComparison.Ordinal) ||
        type.Name.Contains("DisplayClass", StringComparison.Ordinal))
        return true;
    return type.CustomAttributes.Any(
        a => a.AttributeType.FullName ==
             "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
}

static void CompareType(TypeDefinition oldType, TypeDefinition newType, List<string> failures) {
    var label = oldType.FullName;
    if (oldType.Attributes != newType.Attributes)
        failures.Add("type attributes/layout changed: " + label + " (" + oldType.Attributes +
                     " -> " + newType.Attributes + ")");
    if (TypeKey(oldType.BaseType) != TypeKey(newType.BaseType))
        failures.Add("base type changed: " + label + " (" + TypeKey(oldType.BaseType) + " -> " +
                     TypeKey(newType.BaseType) + ")");

    var oldInterfaces = oldType.Interfaces.Select(i => TypeKey(i.InterfaceType))
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .ToArray();
    var newInterfaces = newType.Interfaces.Select(i => TypeKey(i.InterfaceType))
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .ToArray();
    if (!oldInterfaces.SequenceEqual(newInterfaces))
        failures.Add("interfaces changed: " + label);

    if (oldType.PackingSize != newType.PackingSize || oldType.ClassSize != newType.ClassSize)
        failures.Add("explicit type layout changed: " + label);

    var oldFields = oldType.Fields;
    var newFields = newType.Fields;
    if (oldFields.Count != newFields.Count)
        failures.Add("field count/layout changed: " + label + " (" + oldFields.Count + " -> " +
                     newFields.Count + ")");
    for (int i = 0; i < Math.Min(oldFields.Count, newFields.Count); i++) {
        var a = oldFields[i];
        var b = newFields[i];
        if (a.Name != b.Name) {
            failures.Add("field order/name changed: " + label + "." + a.Name + " -> " + b.Name);
            continue;
        }
        var fieldLabel = label + "." + a.Name;
        if (TypeKey(a.FieldType) != TypeKey(b.FieldType))
            failures.Add("field type changed: " + fieldLabel + " (" + TypeKey(a.FieldType) +
                         " -> " + TypeKey(b.FieldType) + ")");
        if (a.Attributes != b.Attributes)
            failures.Add("field attributes/layout changed: " + fieldLabel);
        if (a.Offset != b.Offset)
            failures.Add("field offset changed: " + fieldLabel + " (" + a.Offset + " -> " +
                         b.Offset + ")");
        if (a.HasConstant != b.HasConstant || (a.HasConstant && !Equals(a.Constant, b.Constant)))
            failures.Add("field constant changed: " + fieldLabel);
        if (AttributeKeys(a.CustomAttributes) != AttributeKeys(b.CustomAttributes))
            failures.Add("serialized/custom field metadata changed: " + fieldLabel);
        if (MarshalKey(a) != MarshalKey(b))
            failures.Add("field marshal metadata changed: " + fieldLabel);
    }

    var newMethods = newType.Methods.GroupBy(MethodKey).ToDictionary(
        g => g.Key, g => new Queue<MethodDefinition>(g));
    foreach (var oldMethod in oldType.Methods) {
        var methodKey = MethodKey(oldMethod);
        if (!newMethods.TryGetValue(methodKey, out var candidates) || candidates.Count == 0) {
            failures.Add("method signature removed: " + label + "." + methodKey);
            continue;
        }
        var newMethod = candidates.Dequeue();
        CompareMethod(oldMethod, newMethod, label, failures);
    }
}

static void CompareMethod(MethodDefinition oldMethod, MethodDefinition newMethod, string typeLabel,
                          List<string> failures) {
    var label = typeLabel + "." + oldMethod.Name;
    if (oldMethod.Attributes != newMethod.Attributes)
        failures.Add("method access/static/flags changed: " + label + " (" + oldMethod.Attributes +
                     " -> " + newMethod.Attributes + ")");
    if (oldMethod.ImplAttributes != newMethod.ImplAttributes)
        failures.Add("method implementation flags changed: " + label);
    if (oldMethod.CallingConvention != newMethod.CallingConvention)
        failures.Add("method calling convention changed: " + label);
    if (oldMethod.HasThis != newMethod.HasThis || oldMethod.ExplicitThis != newMethod.ExplicitThis)
        failures.Add("method instance signature changed: " + label);
    if (MethodAttributeKeys(oldMethod.CustomAttributes) !=
        MethodAttributeKeys(newMethod.CustomAttributes))
        failures.Add("method custom metadata changed: " + label);
    if (TypeKey(oldMethod.ReturnType) != TypeKey(newMethod.ReturnType) ||
        AttributeKeys(oldMethod.MethodReturnType.CustomAttributes) !=
            AttributeKeys(newMethod.MethodReturnType.CustomAttributes))
        failures.Add("method return signature/metadata changed: " + label);
    if (oldMethod.GenericParameters.Count != newMethod.GenericParameters.Count)
        failures.Add("method generic arity changed: " + label);
    else
        for (int i = 0; i < oldMethod.GenericParameters.Count; i++)
            if (GenericParameterKey(oldMethod.GenericParameters[i]) !=
                GenericParameterKey(newMethod.GenericParameters[i]))
                failures.Add("method generic parameter changed: " + label + " !!" + i);

    if (oldMethod.Parameters.Count != newMethod.Parameters.Count) {
        failures.Add("method parameter count changed: " + label);
        return;
    }
    for (int i = 0; i < oldMethod.Parameters.Count; i++) {
        var a = oldMethod.Parameters[i];
        var b = newMethod.Parameters[i];
        if (TypeKey(a.ParameterType) != TypeKey(b.ParameterType) || a.Attributes != b.Attributes ||
            a.Name != b.Name || a.HasConstant != b.HasConstant ||
            (a.HasConstant && !Equals(a.Constant, b.Constant)) ||
            AttributeKeys(a.CustomAttributes) != AttributeKeys(b.CustomAttributes))
            failures.Add("method parameter signature/metadata changed: " + label + " parameter " +
                         i);
    }
}

static string MethodKey(MethodDefinition method) =>
    method.Name + "`" + method.GenericParameters.Count +
    "(" + string.Join(",", method.Parameters.Select(p => TypeKey(p.ParameterType))) + ")->" +
    TypeKey(method.ReturnType);

static string GenericParameterKey(GenericParameter parameter) =>
    parameter.Name + ":" + parameter.Attributes + ":" +
    string.Join(",", parameter.Constraints.Select(c => TypeKey(c.ConstraintType))
                         .OrderBy(s => s, StringComparer.Ordinal)) +
    ":" + AttributeKeys(parameter.CustomAttributes);

static string TypeKey(TypeReference? type) {
    if (type == null)
        return "<none>";
    if (type is GenericParameter gp)
        return (gp.Type == GenericParameterType.Method ? "!!" : "!") + gp.Position;
    if (type is GenericInstanceType generic)
        return TypeKey(generic.ElementType) + "[" +
               string.Join(",", generic.GenericArguments.Select(TypeKey)) + "]";
    if (type is ArrayType array)
        return TypeKey(array.ElementType) + "[" + array.Rank + ":" +
               string.Join(",",
                           array.Dimensions.Select(d => (d.LowerBound?.ToString() ?? "") + ".." +
                                                        (d.UpperBound?.ToString() ?? ""))) +
               "]";
    if (type is ByReferenceType byref)
        return TypeKey(byref.ElementType) + "&";
    if (type is PointerType pointer)
        return TypeKey(pointer.ElementType) + "*";
    if (type is PinnedType pinned)
        return TypeKey(pinned.ElementType) + " pinned";
    if (type is SentinelType sentinel)
        return TypeKey(sentinel.ElementType) + " sentinel";
    if (type is RequiredModifierType required)
        return "modreq(" + TypeKey(required.ModifierType) + ")" + TypeKey(required.ElementType);
    if (type is OptionalModifierType optional)
        return "modopt(" + TypeKey(optional.ModifierType) + ")" + TypeKey(optional.ElementType);
    return type.FullName + "@" + ScopeIdentity(type.Scope);
}

static string ScopeIdentity(IMetadataScope? scope) => scope switch {
    AssemblyNameReference assembly => AssemblyIdentity(assembly),
    ModuleDefinition module => AssemblyIdentity(module.Assembly.Name),
    ModuleReference module => "module:" + module.Name,
    _ => scope?.Name ?? "<no-scope>"
};

static string AttributeKeys(IEnumerable<CustomAttribute> attributes) => string.Join(
    "|",
    attributes.Select(
        a => TypeKey(a.AttributeType) + "(" +
             string.Join(",", a.ConstructorArguments.Select(ArgumentKey)) + ")" + "[" +
             string.Join(",", a.Fields.Select(f => f.Name + "=" + ArgumentKey(f.Argument))) + "]" +
             "{" +
             string.Join(",", a.Properties.Select(p => p.Name + "=" + ArgumentKey(p.Argument))) +
             "}"));

static string
MethodAttributeKeys(IEnumerable<CustomAttribute> attributes) => AttributeKeys(attributes.Where(
    a => a.AttributeType.FullName !=
             "System.Runtime.CompilerServices.IteratorStateMachineAttribute" &&
         a.AttributeType.FullName != "System.Runtime.CompilerServices.AsyncStateMachineAttribute" &&
         a.AttributeType.FullName !=
             "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"));

static string ArgumentKey(CustomAttributeArgument argument) => TypeKey(argument.Type) + ":" +
                                                               ValueKey(argument.Value);

static string ValueKey(object? value) {
    if (value == null)
        return "null";
    if (value is CustomAttributeArgument argument)
        return ArgumentKey(argument);
    if (value is CustomAttributeArgument[] arguments)
        return "[" + string.Join(",", arguments.Select(ArgumentKey)) + "]";
    if (value is TypeReference type)
        return "type:" + TypeKey(type);
    if (value is IFormattable formattable)
        return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    return value.ToString() ?? "";
}

static string MarshalKey(FieldDefinition field) {
    if (!field.HasMarshalInfo)
        return "<none>";
    var marshal = field.MarshalInfo;
    return marshal.NativeType + ":" +
           (marshal is ArrayMarshalInfo array
                ? array.ElementType + ":" + array.SizeParameterIndex + ":" + array.Size
                : "");
}

static string AssemblyIdentity(AssemblyNameReference name) => string.Join(
    ",", name.Name, name.Version, string.IsNullOrEmpty(name.Culture) ? "neutral" : name.Culture,
    name.PublicKeyToken == null || name.PublicKeyToken.Length == 0
        ? "null"
        : Convert.ToHexString(name.PublicKeyToken),
    name.Attributes);
