from pathlib import Path

root = Path(__file__).resolve().parents[3]
s = (
    root
    / "recovered-full/ExportedProject/Assets/Plugins/Assembly-CSharp-firstpass/ForceTubeVRInterface.cs"
).read_text()
s = (
    s.replace("using UnityEngine;", "")
    .replace(" : MonoBehaviour", "")
    .replace("\t[RuntimeInitializeOnLoadMethod]\n", "")
)
for name in [
    "InitAsync",
    "Kick",
    "Rumble",
    "Shoot",
    "SetActiveResearch",
    "GetBatteryLevel",
    "TempoToKickPower",
]:
    import re

    pattern = r"((?:private|public) static (void|byte) " + name + r"\([^)]*\)\s*\{)"

    def inject(m):
        action = (
            "return;"
            if m[2] == "void"
            else 'throw new NotSupportedException("ForceTube unavailable");'
        )
        return (
            m[1]
            + "\n        if(NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()){"
            + action
            + "}"
        )

    s, n = re.subn(pattern, inject, s)
    assert n == 1, name
s += """
namespace NativeH3VR { public static class NativeSteamVRBootstrap {
 public static bool ForceTubeUnavailable(){return Environment.GetEnvironmentVariable("FORCETUBE_FIXTURE_UNAVAILABLE")=="1";}
}}
public static class ForceTubeFixture {
 [DllImport("ForceTubeVR_API_x64",EntryPoint="GetCount")]static extern int NativeCount();
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]static int Count(){return NativeCount();}
 public static void Main(){
  typeof(ForceTubeVRInterface).GetMethod("InitAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,null);
  ForceTubeVRInterface.Kick(11);ForceTubeVRInterface.Rumble(12,0.5f);ForceTubeVRInterface.Shoot(13,14,0.6f);ForceTubeVRInterface.SetActiveResearch(true);
  if(NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()){
   int rejected=0;try{ForceTubeVRInterface.GetBatteryLevel();}catch(NotSupportedException){rejected++;}
   try{ForceTubeVRInterface.TempoToKickPower(2);}catch(NotSupportedException){rejected++;}
   if(rejected!=2)throw new Exception("Unavailable queries fabricated success");
   Console.WriteLine("PASS unavailable: optional commands disabled, both data queries reject");
  }else{
   if(ForceTubeVRInterface.GetBatteryLevel()!=42 || ForceTubeVRInterface.TempoToKickPower(2)!=17 || Count()!=7)throw new Exception("Supported native behavior changed");
   Console.WriteLine("PASS supported: all seven original native calls executed with original results");
  }
 }
}
"""
(root / "platform/optional-hardware/fixture/Fixture.cs").write_text(s)
