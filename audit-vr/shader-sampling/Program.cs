using DXDecompiler;
using System.Text.Json;
using System.Text.RegularExpressions;
string? line;
while ((line = Console.ReadLine()) != null) {
    try {
        using var input = JsonDocument.Parse(line);
        var id = input.RootElement.GetProperty("sha256").GetString();
        var asm = new BytecodeContainer(
                      Convert.FromBase64String(input.RootElement.GetProperty("code").GetString()!))
                      .ToString();
        var samples = new List<object>();
        int sampleCount = 0;
        foreach (var raw in asm.Split('\n')) {
            var s = raw.Trim();
            if (!s.StartsWith("sample") && !s.StartsWith("gather4") &&
                !s.StartsWith("ld_indexable") && !s.StartsWith("ld_aoffimmi"))
                continue;
            sampleCount++;
            var m = Regex.Match(s, @"\s([ro]\d+)\.([xyzw]+),.*?\b(t\d+)\.([xyzw]+)(?:,|\s)");
            bool gather = s.StartsWith("gather4"), offset = s.Contains("aoffimmi"),
                 compare = s.StartsWith("sample_c");
            string? expected = null, implicitResult = null;
            if (m.Success && !gather && !compare) {
                var dest = m.Groups[2].Value;
                var sw = m.Groups[4].Value;
                if (sw.Length == 1)
                    sw = new string(sw[0], 4);
                if (sw.Length == 4) {
                    expected = string.Concat(dest.Select(c => sw["xyzw".IndexOf(c)]));
                    implicitResult = "xyzw"[..dest.Length];
                }
            }
            if (gather || offset || expected != null && expected != implicitResult)
                samples.Add(
                    new { instruction = s, gather, offset,
                          resultSwizzleLoss = expected != null && expected != implicitResult,
                          expected, implicitResult });
        }
        Console.WriteLine(
            JsonSerializer.Serialize(new { sha256 = id, sampleCount, operations = samples }));
    } catch (Exception ex) {
        Console.WriteLine(JsonSerializer.Serialize(new { error = ex.ToString() }));
    }
}
