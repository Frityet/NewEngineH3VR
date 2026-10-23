using DXDecompiler;
using System.Text.Json;
using System.Text.RegularExpressions;
string? line;
while ((line = Console.ReadLine()) != null) {
    try {
        using var input = JsonDocument.Parse(line);
        var asm = new BytecodeContainer(
                      Convert.FromBase64String(input.RootElement.GetProperty("code").GetString()!))
                      .ToString();
        var modes = new Dictionary<string, string>();
        var operations = new List<object>();
        foreach (var raw in asm.Split('\n')) {
            var s = raw.Trim();
            var d = Regex.Match(s, @"^dcl_sampler\s+s(\d+),\s*(\w+)");
            if (d.Success)
                modes[d.Groups[1].Value] = d.Groups[2].Value;
            if (!s.StartsWith("sample") && !s.StartsWith("gather4"))
                continue;
            var m = Regex.Match(s, @"\bt(\d+)(?:\.[xyzw]+)?,\s*s(\d+)");
            if (!m.Success)
                throw new Exception("Unparsed sampling instruction: " + s);
            bool compare =
                s.StartsWith("sample_c") || Regex.IsMatch(s, @"^gather4(?:_po)?_c(?:\b|_)");
            operations.Add(new { instruction = s, resource = int.Parse(m.Groups[1].Value),
                                 sampler = int.Parse(m.Groups[2].Value), compare });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { modes, operations }));
    } catch (Exception ex) {
        Console.WriteLine(JsonSerializer.Serialize(new { error = ex.ToString() }));
    }
}
