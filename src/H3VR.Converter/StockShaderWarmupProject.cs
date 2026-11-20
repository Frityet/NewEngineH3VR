using Mono.Cecil;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class StockShaderWarmupProject
{
    private const string Asset = "Assets/ShaderVariantCollection/H3_ShaderAsset3.shadervariants";
    private const string Manager = "Assets/Resources/prefabs/_managers/_GameManager.prefab";
    private const string Graphics = "ProjectSettings/GraphicsSettings.asset";
    private const string FileId = "20000000";

    private static readonly Regex Preloads = new(@"(?m)^  m_PreloadedShaders:[ \t]*(?<eol>\r?\n)(?<rows>(?:  - [^\r\n]+\r?\n)+)");
    private static readonly Regex AssetGuid = new(@"(?m)^guid: (?<guid>[a-fA-F0-9]{32})\r?$");

    internal static bool Apply(string project, string game)
    {
        if (!TryPrepare(project, out var settings))
            return false;
        using var assembly = AssemblyDefinition.ReadAssembly(game);
        var manager = assembly.MainModule.GetType("FistVR.GM");
        var awake = manager?.Methods.SingleOrDefault(m => m.Name == "Awake" && m.HasBody && m.HasThis && m.Parameters.Count == 0);
        if (awake == null || !awake.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference method && method.Name == "Queue" &&
            method.DeclaringType.FullName == "NativeH3VR.StockShaderWarmup" &&
            method.DeclaringType.Scope.Name == "NativeH3VR.OpenXR"))
            return false;
        File.WriteAllText(Path.Combine(project, Graphics), settings);
        return true;
    }

    internal static bool TryPrepare(string project, out string updatedSettings)
    {
        updatedSettings = null!;
        string asset = Path.Combine(project, Asset), manager = Path.Combine(project, Manager);
        string graphics = Path.Combine(project, Graphics);
        if (new[] { asset, asset + ".meta", manager, graphics }.Any(path => !File.Exists(path)))
            return false;
        using var collection = File.OpenText(asset);
        var header = Enumerable.Range(0, 16).Select(_ => collection.ReadLine()).TakeWhile(line => line != null).ToArray();
        int objectStart = Array.IndexOf(header, $"--- !u!200 &{FileId}");
        if (objectStart < 0 || objectStart + 1 >= header.Length ||
            header[objectStart + 1] != "ShaderVariantCollection:" ||
            !header.Skip(objectStart + 2).TakeWhile(line => line?.StartsWith("  ", StringComparison.Ordinal) == true)
                .Contains("  m_Name: H3_ShaderAsset3"))
            return false;

        var ids = AssetGuid.Matches(File.ReadAllText(asset + ".meta"));
        if (ids.Count != 1)
            return false;
        string pointer = $"{{fileID: {FileId}, guid: {ids[0].Groups["guid"].Value}, type: 2}}";
        if (!Regex.IsMatch(File.ReadAllText(manager), @"(?m)^  ShaderVariants: " + Regex.Escape(pointer) + @"\r?$"))
            return false;

        var stockRow = new Regex(@"(?m)^  - " + Regex.Escape(pointer) + @"[ \t]*\r?\n");
        string original = File.ReadAllText(graphics);
        var section = Preloads.Matches(original);
        if (section.Count != 1 || stockRow.Matches(section[0].Groups["rows"].Value).Count != 1)
            return false;
        var match = section[0];
        string remainder = stockRow.Replace(match.Groups["rows"].Value, "", 1);
        string eol = match.Groups["eol"].Value;
        string replacement = remainder.Length == 0
            ? "  m_PreloadedShaders: []" + eol : "  m_PreloadedShaders:" + eol + remainder;
        updatedSettings = original[..match.Index] + replacement + original[(match.Index + match.Length)..];
        return true;
    }
}
