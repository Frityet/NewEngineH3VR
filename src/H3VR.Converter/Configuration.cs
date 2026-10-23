using System.Text.Json;
using System.Text.Json.Serialization;

namespace H3VR.Converter;

public sealed record ConversionConfig
{
    public const string TargetVersion = "2021.3.45f2";
    public string GameDirectory { get; init; } = "";
    public string UnityEditor { get; init; } = "";
    public string Workspace { get; init; } = "";
    public string? RecoveredProject { get; init; }
    public string? ShaderSources { get; init; }
    public string? AssetRipperUrl { get; init; }
    public string? IntegrationManifest { get; init; }
    public string BuildTarget { get; init; } = "StandaloneWindows64";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static ConversionConfig Load(string file)
    {
        var config = JsonSerializer.Deserialize<ConversionConfig>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("Configuration is empty.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string Absolute(string value) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), directory);
        return config with
        {
            GameDirectory = Absolute(config.GameDirectory),
            UnityEditor = Absolute(config.UnityEditor),
            Workspace = Absolute(config.Workspace),
            RecoveredProject = config.RecoveredProject is null ? null : Absolute(config.RecoveredProject),
            ShaderSources = config.ShaderSources is null ? null : Absolute(config.ShaderSources),
            IntegrationManifest = config.IntegrationManifest is null ? null : Absolute(config.IntegrationManifest)
        };
    }
    [JsonIgnore]
    public string GameData => Path.Combine(GameDirectory, "h3vr_Data");
    [JsonIgnore]
    public string ManagedInput => Path.Combine(GameData, "Managed");
    [JsonIgnore]
    public string Project => Path.Combine(Workspace, "project");
    [JsonIgnore]
    public string ManagedOutput => Path.Combine(Workspace, "managed");
    [JsonIgnore]
    public string Export => RecoveredProject ?? Path.Combine(Workspace, "recovery", "ExportedProject");

    [JsonIgnore]
    public string UnityData
    {
        get
        {
            var parent = Path.GetDirectoryName(UnityEditor)!;
            return OperatingSystem.IsMacOS() && Path.GetFileName(parent) == "MacOS"
                ? Path.GetDirectoryName(parent)! : Path.Combine(parent, "Data");
        }
    }
    [JsonIgnore]
    public string EngineAssemblies => Path.Combine(UnityData, "Managed", "UnityEngine");

    public void Validate(string repository)
    {
        if (!File.Exists(Path.Combine(ManagedInput, "Assembly-CSharp.dll")))
            throw new InvalidDataException("GameDirectory must be your installed H3VR folder containing h3vr_Data/Managed/Assembly-CSharp.dll.");
        if (!File.Exists(UnityEditor) || !File.Exists(Path.Combine(EngineAssemblies, "UnityEngine.CoreModule.dll")))
            throw new InvalidDataException("UnityEditor must point to the Unity 2021.3.45f2 executable, including its managed modules.");
        foreach (var input in new[] { GameDirectory, UnityData, repository, RecoveredProject, ShaderSources }.OfType<string>())
            SafeFiles.RequireSeparate(input, Workspace);
        if (BuildTarget is not ("StandaloneWindows64" or "StandaloneOSX" or "StandaloneLinux64"))
            throw new InvalidDataException("BuildTarget must be StandaloneWindows64, StandaloneOSX or StandaloneLinux64.");
        if (BuildTarget != "StandaloneWindows64" && IntegrationManifest is null)
            throw new InvalidDataException("Non-Windows builds require a compatible platform integration manifest; Windows native plugins cannot run on other platforms.");
        if (AssetRipperUrl is not null && (!Uri.TryCreate(AssetRipperUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http"))
            throw new InvalidDataException("AssetRipperUrl must be a local HTTP service (for example http://127.0.0.1:58947/).");
        if (RecoveredProject is not null && !Directory.Exists(Path.Combine(RecoveredProject, "Assets")))
            throw new InvalidDataException("RecoveredProject must contain Assets. Recovery input is never modified.");
    }

    public void VerifyEditorVersion()
    {
        // macOS gives an authoritative version without starting the editor. Other hosts are
        // checked by the editor entry point before it imports or builds any game scenes.
        var plist = Path.Combine(UnityData, "Info.plist");
        if (!File.Exists(plist)) return;
        var document = System.Xml.Linq.XDocument.Load(plist);
        var key = document.Descendants("key").FirstOrDefault(k => k.Value == "CFBundleVersion");
        var value = key?.ElementsAfterSelf().FirstOrDefault()?.Value;
        if (value != TargetVersion)
            throw new InvalidDataException($"Expected Unity {TargetVersion}; this editor is {value ?? "unknown"}.");
    }
}

public static class Repository
{
    public static string Find()
    {
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(candidate); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "H3VR.Converter.sln"))) return directory.FullName;
        throw new InvalidOperationException("Run from the converter checkout (H3VR.Converter.sln was not found).");
    }
}
