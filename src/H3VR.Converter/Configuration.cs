using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

public sealed record ConversionConfig
{
    public const string TargetVersion = "6000.7.0b10";
    public string UnityVersion { get; init; } = TargetVersion;
    public string GameDirectory { get; init; } = "";
    public string UnityEditor { get; init; } = "";
    public string Workspace { get; init; } = "";
    public string? RecoveredProject { get; init; }
    public string? ExtractionWorkspace { get; init; }
    public string? ShaderSources { get; init; }
    public string? DxbcCompiler { get; init; }
    public string? SpirvCross { get; init; }
    public string? ToolCacheDirectory { get; init; }
    public bool Offline { get; init; }
    public string? IntegrationManifest { get; init; }
    public string? OpenXrHeaders { get; init; }
    public string? CSteamworks { get; init; }
    public string? SteamworksHeaders { get; init; }
    public string? SteamApi { get; init; }
    public string? FmodSdk { get; init; }
    public bool BuildProbe { get; init; }
    public bool CompactPlayerData { get; init; }
    public bool GraphicsUpgrade { get; init; }
    public string BuildTarget { get; init; } = OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "StandaloneOSX" : "StandaloneWindows64";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static ConversionConfig Load(string file)
    {
        var config = JsonSerializer.Deserialize<ConversionConfig>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("Configuration is empty.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return config.ResolvePaths(directory);
    }

    public ConversionConfig ResolvePaths(string directory)
    {
        string Absolute(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.Length > 2 ? value[2..] : "");
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), directory);
        }
        string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : Absolute(value);
        return this with
        {
            GameDirectory = Absolute(GameDirectory),
            UnityEditor = Absolute(UnityEditor),
            Workspace = Absolute(Workspace),
            RecoveredProject = Optional(RecoveredProject),
            ExtractionWorkspace = Optional(ExtractionWorkspace),
            ShaderSources = Optional(ShaderSources),
            DxbcCompiler = Optional(DxbcCompiler),
            SpirvCross = Optional(SpirvCross),
            IntegrationManifest = Optional(IntegrationManifest),
            OpenXrHeaders = Optional(OpenXrHeaders),
            CSteamworks = Optional(CSteamworks),
            SteamworksHeaders = Optional(SteamworksHeaders),
            SteamApi = Optional(SteamApi),
            FmodSdk = Optional(FmodSdk),
            ToolCacheDirectory = Optional(ToolCacheDirectory)
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
    public string Export => RecoveredProject ?? Path.Combine(ExtractionWorkspace ?? Workspace, "recovery", "ExportedProject");

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
    public string UnityScripting => Path.Combine(UnityData, "Resources", "Scripting");
    [JsonIgnore] public string EngineAssemblies => Path.Combine(UnityScripting, "Managed", "UnityEngine");
    [JsonIgnore] public string MonoReferenceAssemblies => Path.Combine(UnityScripting, "MonoBleedingEdge", "lib", "mono", "4.7.1-api");
    [JsonIgnore]
    public string MacPlayerAssemblies => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(UnityData)!)!,
        "PlaybackEngines", "MacStandaloneSupport", "Variations", "mono", "Managed");

    public static bool IsUnitySixSeven(string? version) => version is not null && Regex.IsMatch(version, @"\A6000\.7\.\d+[abfp]\d+\z");

    public static void RequireSupportedUnityVersion(string version)
    {
        if (!IsUnitySixSeven(version))
            throw new InvalidDataException($"Choose an exact Unity 6.7 editor version such as {TargetVersion}.");
    }

    internal string CacheDirectory(string repository) =>
        ToolCacheDirectory ?? DefaultCacheDirectory(repository);

    private static string DefaultCacheDirectory(string repository) =>
        Path.Combine(Path.GetFullPath(repository), "cache");

    internal void ValidateToolCache(string repository, params string?[] inputs)
    {
        var cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CacheDirectory(repository)));
        SafeFiles.RequireToolCachePath(repository, cache);

        foreach (var input in inputs.OfType<string>())
            SafeFiles.RequireSeparate(input, cache);
    }

    public void Validate(string repository, string through = "validate")
    {
        RequireSupportedUnityVersion(UnityVersion);
        RequireColdRecovery(through);
        if (string.IsNullOrWhiteSpace(Workspace)) throw new InvalidDataException("Set --workspace to an empty directory under the checkout's .local directory.");
        SafeFiles.RequirePrivateOutput(repository, Workspace);
        if (!File.Exists(Path.Combine(ManagedInput, "Assembly-CSharp.dll")))
            throw new InvalidDataException("Set --game to your installed H3VR folder containing h3vr_Data/Managed/Assembly-CSharp.dll, or use --tui.");
        if (BuildTarget == "StandaloneOSX") PlayerReceipt.ValidateSupportedInstallation(GameDirectory);
        if (through != "extract" && (!File.Exists(UnityEditor) || !File.Exists(Path.Combine(EngineAssemblies, "UnityEngine.CoreModule.dll"))))
            throw new InvalidDataException($"Set --unity to an installation of Unity {UnityVersion} containing its scripting assemblies, or use --tui.");
        if (through is not ("extract" or "managed"))
        {
            GraphicsUpgradeProject.RequireWorkspaceMode(this);
            if (GraphicsUpgrade) GraphicsUpgradeProject.VerifyEditorPackage(this);
        }
        foreach (var input in new[] { GameDirectory, string.IsNullOrEmpty(UnityEditor) ? null : UnityData, RecoveredProject, ExtractionWorkspace, ShaderSources, IntegrationManifest is null ? null : Path.GetDirectoryName(IntegrationManifest) }.OfType<string>())
            SafeFiles.RequireSeparate(input, Workspace);
        if (BuildTarget is not ("StandaloneWindows64" or "StandaloneOSX" or "StandaloneLinux64"))
            throw new InvalidDataException("BuildTarget must be StandaloneWindows64, StandaloneOSX or StandaloneLinux64.");
        if (BuildProbe && (BuildTarget != "StandaloneLinux64" || IntegrationManifest is not null))
            throw new InvalidDataException("--build-probe requires --target linux without a platform integration.");
        if (through is not ("extract" or "managed") && IntegrationManifest is null && !BuildProbe)
        {
            if (BuildTarget == "StandaloneOSX") LocalPlatformPreparation.Validate(repository, this);
            if (BuildTarget == "StandaloneLinux64")
                throw new InvalidDataException("Linux builds require a compatible platform integration manifest.");
        }
        if ((DxbcCompiler is null) != (SpirvCross is null))
            throw new InvalidDataException("Use --dxbc-compiler and --spirv-cross together to generate a local shader overlay.");
        if (DxbcCompiler is not null)
        {
            if (ShaderSources is not null || RecoveredProject is not null)
                throw new InvalidDataException("Automatic shader generation requires converter extraction data. Omit --shaders and --recovered when supplying translators.");
            if (!File.Exists(DxbcCompiler) || !File.Exists(SpirvCross))
                throw new InvalidDataException("Set --dxbc-compiler and --spirv-cross to the installed translator executables.");
        }
        ValidateToolCache(repository, GameDirectory, string.IsNullOrEmpty(UnityEditor) ? null : UnityData, Workspace, RecoveredProject, ExtractionWorkspace, ShaderSources);
        if (NeedsClassDatabase(through))
            PlayerEnlighten.CheckClassDatabase(CacheDirectory(repository), required: Offline);
        if (RecoveredProject is not null && !Directory.Exists(Path.Combine(RecoveredProject, "Assets")))
            throw new InvalidDataException("RecoveredProject must contain Assets. Recovery input is never modified.");
    }

    internal bool PreparesShaders(string through) =>
        through is "project" or "validate" or "build" && ShaderSources is null && RecoveredProject is null &&
        (!BuildProbe || DxbcCompiler is not null);

    internal bool NeedsClassDatabase(string through) =>
        through == "build" && BuildTarget == "StandaloneOSX" || PreparesShaders(through);

    internal void RequireColdRecovery(string through)
    {
        if (ExtractionWorkspace is not null && RecoveredProject is not null)
            throw new InvalidDataException("Choose either --reuse-extraction or --recovered.");
        if (BuildTarget == "StandaloneOSX" && RecoveredProject is not null && through is ("project" or "validate" or "build"))
            throw new InvalidDataException("macOS project and player stages require the converter's own cold extraction; --recovered is comparison-only through managed. Choose a fresh workspace without --recovered.");
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
        if (value != UnityVersion)
            throw new InvalidDataException($"Expected Unity {UnityVersion}; this editor is {value ?? "unknown"}.");
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
