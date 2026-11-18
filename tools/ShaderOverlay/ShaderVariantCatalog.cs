using System.Globalization;

namespace NativeH3VR.ShaderOverlay;

internal static class ShaderVariantCatalog
{
    internal sealed record Catalog(int SchemaVersion, Shader[] Shaders);
    internal sealed record Shader(string Path, string Guid, Group[] Passes);
    internal sealed record Group(string LightMode, string Name, Domain[] Domains);
    internal sealed record Domain(int SubshaderIndex, int PassIndex, string[] Keywords, string[] Fixed, string[] Rows);
    internal sealed record Pass(string LightMode, string Name, Domain? Domain);

    internal static Group[] CompleteGroups(IEnumerable<Pass> passes) =>
        passes.GroupBy(pass => (pass.LightMode, pass.Name))
            .Where(group => group.All(pass => pass.Domain is not null))
            .Select(group => new Group(group.Key.LightMode, group.Key.Name, group.Select(pass => pass.Domain!).ToArray()))
            .ToArray();

    internal static Domain FromRows(int subshader, int pass, string[] names, string[] fixedNames, List<ulong> rows) =>
        new(subshader, pass, names, fixedNames, rows.Order().Select(row => row.ToString("x", CultureInfo.InvariantCulture)).ToArray());

    internal static string? NormalizeMode(string? lightMode)
    {
        var normalized = lightMode?.Trim().ToUpperInvariant();
        return normalized switch
        {
            null or "" or "ALWAYS" => "ALWAYS",
            "FORWARDBASE" or "FORWARDADD" or "SHADOWCASTER" or "DEFERRED" or "META" or
                "VERTEX" or "VERTEXLM" or "VERTEXLMRGBM" or "PREPASSBASE" or "PREPASSFINAL" or "MOTIONVECTORS" => normalized,
            _ => null
        };
    }
}
