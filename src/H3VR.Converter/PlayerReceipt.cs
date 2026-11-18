using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace H3VR.Converter;

/// <summary>Records only local build metadata; never copies the Steam manifest or account fields.</summary>
internal static class PlayerReceipt
{
    internal static string SteamBuildId(string game)
    {
        var common = Directory.GetParent(Path.GetFullPath(game));
        var steamapps = common?.Parent;
        if (common is null || steamapps is null || !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A macOS player build requires the actual Steam library installation and its appmanifest_450540.acf.");
        var manifest = Path.Combine(steamapps.FullName, "appmanifest_450540.acf");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Steam app manifest is required to identify the build.", manifest);
        var text = File.ReadAllText(manifest);
        string Field(string name)
        {
            var matches = Regex.Matches(text, "(?im)^\\s*\\\"" + Regex.Escape(name) + "\\\"\\s+\\\"([^\\\"]+)\\\"");
            if (matches.Count != 1) throw new InvalidDataException("Steam manifest has missing or ambiguous " + name + ".");
            return matches[0].Groups[1].Value;
        }
        if (Field("appid") != "450540" || !Field("installdir").Equals(Path.GetFileName(game), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam manifest does not identify this H3VR installation.");
        var build = Field("buildid");
        if (!Regex.IsMatch(build, "^[0-9]+$")) throw new InvalidDataException("Steam build ID is invalid.");
        return build;
    }

    internal static string Write(string app, string game)
    {
        var contents = Path.Combine(app, "Contents");
        var plist = XDocument.Load(Path.Combine(contents, "Info.plist"));
        var key = plist.Descendants("key").FirstOrDefault(x => x.Value == "CFBundleExecutable");
        var executable = key?.ElementsAfterSelf().FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(executable) || Path.GetFileName(executable) != executable || !File.Exists(Path.Combine(contents, "MacOS", executable)))
            throw new InvalidDataException("Player executable missing or invalid in Info.plist.");
        var managed = Path.Combine(contents, "Resources", "Data", "Managed");
        if (!Directory.Exists(managed)) throw new InvalidDataException("Player managed assemblies directory is missing.");
        var assemblies = Directory.GetFiles(managed, "*.dll").Order(StringComparer.Ordinal).ToDictionary(Path.GetFileName, SafeFiles.Hash);
        if (!assemblies.ContainsKey("Assembly-CSharp.dll")) throw new InvalidDataException("Player is missing Assembly-CSharp.dll.");
        var path = Path.Combine(contents, "Resources", "nativeh3vr-build.json");
        SafeFiles.AtomicJson(path, new { schemaVersion = 1, unityVersion = ConversionConfig.TargetVersion, buildTarget = "StandaloneOSX", architecture = "arm64", playerExecutable = executable, managedAssemblies = assemblies, steamBuildId = SteamBuildId(game) });
        return path;
    }
}
