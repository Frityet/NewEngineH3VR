using System.Text.RegularExpressions;

namespace H3VR.Converter;

public static partial class PlayerDataFiles
{
    [GeneratedRegex(@"^(globalgamemanagers(?:\.assets)?|level[0-9]+|(?:resources|sharedassets[0-9]+)\.assets)(?:\.resS)?$")]
    private static partial Regex LooseAssetName();

    public static List<string> Gather(string data)
    {
        var names = Directory.EnumerateFiles(data).Select(path => Path.GetFileName(path))
            .Where(name => LooseAssetName().IsMatch(name)).Order(StringComparer.Ordinal).ToList();
        if (!names.Remove("globalgamemanagers"))
            throw new InvalidDataException("Missing loose Unity player data: " + data);
        names.Insert(0, "globalgamemanagers");
        const string builtIn = "Resources/unity_builtin_extra";
        if (File.Exists(Path.Combine(data, builtIn))) names.Insert(1, builtIn);
        string lighting = Path.Combine(data, "GI");
        if (Directory.Exists(lighting))
        {
            if (new DirectoryInfo(lighting).LinkTarget is not null)
                throw new InvalidDataException("Player lighting directory must be local to its player.");
            names.AddRange(SafeFiles.Files(lighting).Select(path =>
                Path.GetRelativePath(data, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal));
        }
        foreach (string name in names)
        {
            string file = SafeFiles.Inside(data, name);
            if (new FileInfo(file).LinkTarget is not null || SafeFiles.Canonical(file) != file)
                throw new InvalidDataException("Linked player data is unsupported: " + name);
        }
        return names;
    }
}
