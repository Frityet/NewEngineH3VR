using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

using var original = AssemblyDefinition.ReadAssembly(args[0]);
using var candidate = AssemblyDefinition.ReadAssembly(args[1]);
using var legacy = AssemblyDefinition.ReadAssembly(args[2]);
var checks = new List<string>();
void Need(bool value, string label) {
    if (!value)
        throw new Exception(label);
    checks.Add(label);
}
TypeDefinition Input(AssemblyDefinition a) => a.MainModule.GetType("NativeH3VR.NativeSteamVRInput");
TypeDefinition
Boot(AssemblyDefinition a) => a.MainModule.GetType("NativeH3VR.NativeSteamVRBootstrap");
MethodDefinition Method(TypeDefinition type,
                        string name) => type.Methods.Single(m => m.Name == name);
string Body(MethodDefinition method) {
    string Operand(object operand) =>
        operand switch { Instruction i => "instruction#" + method.Body.Instructions.IndexOf(i),
                         Instruction[] rows => string.Join(
                             ",", rows.Select(i => method.Body.Instructions.IndexOf(i))),
                         _ => operand?.ToString() ?? "" };
    return string.Join("\n",
                       method.Body.Instructions.Select(i => i.OpCode + " " + Operand(i.Operand)));
}
bool Calls(MethodDefinition m, string owner, string name) => m.Body.Instructions.Any(
    i => i.Operand is MethodReference r && r.DeclaringType.FullName == owner && r.Name == name);
foreach (string name in new[] { "GateInput", "RefreshTracking", "SetFixture", "SetFocusFixture",
                                "EndFixture", "Role", "Source", "Digital", "Analog", "Pose",
                                "ResolveSkeleton", "GetSkeletalSummaryData",
                                "ClearInactiveSkeletonSummary" }) {
    Need(Body(Method(Input(candidate), name)) == Body(Method(Input(legacy), name)),
         "unchanged gated/off-gate body: " + name);
    Need(Body(Method(Input(original), name)) == Body(Method(Input(legacy), name)),
         "unchanged installed/off-gate body: " + name);
}
foreach (var type in original.MainModule.Types.Where(t => t.Name != "<Module>")) {
    var other = candidate.MainModule.GetType(type.FullName);
    Need(other != null, "retained type " + type.FullName);
    string Fields(TypeDefinition t) => string.Join(
        "\n", t.Fields.Select(f => f.Name + " " + f.FieldType.FullName + " " + f.Attributes));
    Need(Fields(type) == Fields(other), "retained exact field layout " + type.FullName);
}
Need(!Calls(Method(Boot(candidate), "get_Enabled"), "System.Environment", "GetEnvironmentVariable"),
     "candidate guard has no environment read");
Need(Calls(Method(Boot(legacy), "get_Enabled"), "System.Environment", "GetEnvironmentVariable"),
     "legacy guard still reads environment");
Need(Method(Boot(candidate), "CreateCore")
             .Body.Instructions.Count(i => i.Operand is MethodReference r &&
                                           r.DeclaringType.Name == "LaunchConfiguration" &&
                                           r.Name == "Refresh") == 1,
     "new session refreshes launch configuration once");
foreach (string name in new[] { "UpdateActionState", "GetDigitalActionData", "GetAnalogActionData",
                                "GetPoseActionDataForNextFrame", "GetOriginTrackedDeviceInfo",
                                "GetSkeletalActionData" })
    Need(Calls(Method(Input(candidate), name), Input(candidate).FullName, "Size"),
         "cached ABI size call: " + name);
Need(Input(candidate).NestedTypes.Any(t => t.Name == "LookupCache") &&
         !Input(legacy).NestedTypes.Any(t => t.Name == "LookupCache"),
     "manifest cache is gated");
Need(Boot(candidate).NestedTypes.Any(t => t.Name == "LaunchConfiguration") &&
         !Boot(legacy).NestedTypes.Any(t => t.Name == "LaunchConfiguration"),
     "launch cache is gated");
var report = new { passed = true, assertions = checks.Count, checks };
File.WriteAllText(args[3],
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Input lookup IL checks pass: " + checks.Count);
