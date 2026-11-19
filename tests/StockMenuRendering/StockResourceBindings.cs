using System.IO;
using UnityEngine;

namespace NativeH3VR.QA
{
    public sealed partial class StockMenuAudit
    {
        static void CaptureStockResourceBindings(string directory)
        {
            // These names are the original Resources.Load addresses used by the stock player.
            // The two bloom shaders also have same-name copies elsewhere in the player.
            string[] addresses = {
                "avprovideo-internal-imgui-transparent", "avprovideo-internal-preview",
                "avprovideo-internal-resolve", "avprovideo-internal-ui-default-stereo",
                "avprovideo-internal-ui-default-transparent", "base", "blend", "bloom",
                "bloomfinal", "depthmask", "depthmaskblend", "mask", "maskblend", "processonly"
            };
            using (var report = new StreamWriter(Path.Combine(directory, "stock-resource-shaders.tsv")))
            {
                foreach (string address in addresses)
                {
                    var shader = Resources.Load<Shader>(address);
                    report.WriteLine(shader ? address + "\t" + shader.name + "\t" + shader.isSupported + "\t" + shader.passCount
                                            : address + "\t(missing)\tFalse\t0");
                }
            }
        }
    }
}
