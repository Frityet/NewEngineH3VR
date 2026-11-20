using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace H3VR.Converter;

/// <summary>Records only local build metadata; never copies the Steam manifest or account fields.</summary>
internal static class PlayerReceipt
{
    internal static void ValidateSupportedInstallation(string game)
    {
        var build = SteamBuildId(game);
        if (build != "25002541") throw new InvalidDataException("Unsupported Steam H3VR build " + build + "; the macOS importer currently targets build 25002541.");
        foreach (var relative in new[] { "h3vr.exe", "steam_api64.dll", "h3vr_Data/globalgamemanagers", "h3vr_Data/Managed/Assembly-CSharp.dll", "h3vr_Data/Managed/Assembly-CSharp-firstpass.dll" })
        {
            var path = Path.Combine(game, relative);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("Required Steam game input is missing or empty: " + relative);
            using var stream = File.OpenRead(path);
            _ = stream.ReadByte();
        }
        if (!Directory.Exists(Path.Combine(game, "h3vr_Data/StreamingAssets")))
            throw new InvalidDataException("Required Steam game input is missing: h3vr_Data/StreamingAssets");
    }

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

    internal static string Write(string app, string game, string extractionReceipt, string unityVersion = ConversionConfig.TargetVersion, bool graphicsUpgrade = false)
    {
        ValidateSupportedInstallation(game);
        var contents = Path.Combine(app, "Contents");
        var plist = XDocument.Load(Path.Combine(contents, "Info.plist"));
        var key = plist.Descendants("key").FirstOrDefault(x => x.Value == "CFBundleExecutable");
        var executable = key?.ElementsAfterSelf().FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(executable) || Path.GetFileName(executable) != executable || !File.Exists(Path.Combine(contents, "MacOS", executable)))
            throw new InvalidDataException("Player executable missing or invalid in Info.plist.");
        var managed = Path.Combine(contents, "Resources", "Data", "Managed");
        if (!Directory.Exists(managed)) throw new InvalidDataException("Player managed assemblies directory is missing.");
        var assemblies = Directory.GetFiles(managed, "*.dll").Order(StringComparer.Ordinal).ToDictionary(path => Path.GetFileName(path)!, SafeFiles.Hash);
        if (!assemblies.ContainsKey("NativeH3VR.Game.dll")) throw new InvalidDataException("Player is missing the retargeted NativeH3VR.Game.dll.");
        if (graphicsUpgrade && !assemblies.ContainsKey(GraphicsUpgradeProject.RuntimeAssembly + ".dll"))
            throw new InvalidDataException("Player is missing the requested graphics upgrade assembly.");
        var path = Path.Combine(contents, "Resources", "nativeh3vr-build.json");
        var extract = JsonSerializer.Deserialize<StepReceipt>(File.ReadAllText(extractionReceipt), ConversionConfig.Json)
            ?? throw new InvalidDataException("Extraction receipt is empty.");
        if (extract.Step != "extract" || !Regex.IsMatch(extract.Fingerprint, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("Extraction receipt does not have a valid extract fingerprint.");
        SafeFiles.AtomicJson(path, new
        {
            schemaVersion = 1,
            modHostSchemaVersion = 2,
            unityVersion,
            buildTarget = "StandaloneOSX",
            architecture = "arm64",
            graphicsPipeline = graphicsUpgrade ? "URP " + GraphicsUpgradeProject.PackageVersion : "Built-in",
            playerExecutable = executable,
            managedAssemblies = assemblies,
            steamBuildId = SteamBuildId(game),
            extractionFingerprint = extract.Fingerprint
        });
        return path;
    }

    internal static void BindReflectionProbes(string playerReceipt, string probesReceipt)
    {
        var stamp = JsonNode.Parse(File.ReadAllText(playerReceipt))?.AsObject()
            ?? throw new InvalidDataException("Empty player build stamp.");
        stamp["reflectionProbesSha256"] = SafeFiles.Hash(probesReceipt);
        SafeFiles.AtomicJson(playerReceipt, stamp);
    }
}
