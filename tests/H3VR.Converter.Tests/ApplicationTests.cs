using H3VR.Converter;
using Spectre.Console.Testing;
using System.Text.Json;

namespace H3VR.Converter.Tests;

public static class ApplicationTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("application resolves JSON paths against the configuration file and flags against the working directory", PathOrigins);
        yield return ("application accepts documented build-target aliases", TargetAliases);
        yield return ("application chooses only unique discovery results automatically", UniqueDiscoveryOnly);
        yield return ("status does not require or create game and Unity paths", StatusIsReadOnly);
        yield return ("offline doctor checks extraction without probing or creating Unity and cache paths", DoctorExtractIsReadOnly);
        yield return ("extract validation does not require a Unity editor", ExtractNeedsNoEditor);
        yield return ("cache and offline changes do not invalidate conversion receipts", CacheSettingsDoNotInvalidateReceipt);
    }

    private static void PathOrigins()
    {
        using var temp = new Temp();
        var configDir = Path.Combine(temp.Root, "settings");
        var cwd = Path.Combine(temp.Root, "run");
        Directory.CreateDirectory(configDir); Directory.CreateDirectory(cwd);
        Write(Path.Combine(cwd, "flag-unity/2021.3.45f2/Editor/Unity.exe"), "synthetic editor marker");
        var configPath = Path.Combine(configDir, "h3vr.json");
        var json = new ConversionConfig
        {
            GameDirectory = "../json-game",
            UnityEditor = "../json-unity/2021.3.45f2/Editor/Unity.exe",
            Workspace = "../json-work",
            ToolCacheDirectory = "../json-cache",
            BuildTarget = "StandaloneWindows64"
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(json, ConversionConfig.Json));
        var loaded = ConversionConfig.Load(configPath);
        var flags = new ConversionOptions
        {
            Game = "flag-game",
            Unity = "flag-unity/2021.3.45f2",
            Workspace = "flag-work",
            Cache = "flag-cache",
            Shaders = "flag-shaders",
            Recovered = "flag-recovered",
            Integration = "flag-integration.json"
        };
        var applied = ConverterApplication.ApplyOptions(loaded, flags, cwd);
        Check(loaded.GameDirectory == Path.GetFullPath(Path.Combine(configDir, "../json-game")), "JSON game path was not resolved relative to its file");
        Check(applied.GameDirectory == Path.GetFullPath(Path.Combine(cwd, "flag-game")), "flag game path was not resolved relative to the command working directory");
        Check(applied.UnityEditor == Path.GetFullPath(Path.Combine(cwd, "flag-unity/2021.3.45f2/Editor/Unity.exe")), "Unity folder flag was not normalized to its executable relative to CWD");
        Check(applied.Workspace == Path.GetFullPath(Path.Combine(cwd, "flag-work")) &&
              applied.ToolCacheDirectory == Path.GetFullPath(Path.Combine(cwd, "flag-cache")) &&
              applied.ShaderSources == Path.GetFullPath(Path.Combine(cwd, "flag-shaders")) &&
              applied.RecoveredProject == Path.GetFullPath(Path.Combine(cwd, "flag-recovered")) &&
              applied.IntegrationManifest == Path.GetFullPath(Path.Combine(cwd, "flag-integration.json")),
            "one or more relative CLI paths did not resolve against CWD or override JSON values");
    }

    private static void TargetAliases()
    {
        using var temp = new Temp();
        var cwd = temp.Root;
        foreach (var (alias, expected) in new[]
        {
            ("windows", "StandaloneWindows64"), ("StandaloneWindows64", "StandaloneWindows64"),
            ("macos", "StandaloneOSX"), ("osx", "StandaloneOSX"), ("StandaloneOSX", "StandaloneOSX"),
            ("linux", "StandaloneLinux64"), ("StandaloneLinux64", "StandaloneLinux64")
        })
        {
            var result = ConverterApplication.ApplyOptions(new ConversionConfig(), new ConversionOptions { Target = alias }, cwd);
            Check(result.BuildTarget == expected, $"target alias '{alias}' resolved to {result.BuildTarget}, expected {expected}");
        }
        Check(Capture(() => ConverterApplication.ApplyOptions(new ConversionConfig(), new ConversionOptions { Target = "android" }, cwd)) is ArgumentException,
            "unsupported target alias was accepted");
    }

    private static void UniqueDiscoveryOnly()
    {
        using var temp = new Temp();
        var config = new ConversionConfig();
        var games = new[] { Path.Combine(temp.Root, "H3VR-one") };
        var editors = new[] { Path.Combine(temp.Root, "Unity-one") };
        var unique = ConverterApplication.WithDefaults(config, games, editors);
        Check(unique.GameDirectory == games[0] && unique.UnityEditor == editors[0], "unique candidates were not selected by default");
        var multiple = ConverterApplication.WithDefaults(config, games.Append(Path.Combine(temp.Root, "H3VR-two")).ToArray(),
            editors.Append(Path.Combine(temp.Root, "Unity-two")).ToArray());
        Check(multiple.GameDirectory.Length == 0 && multiple.UnityEditor.Length == 0,
            "application silently chose among multiple game/editor candidates");
        Check(unique.Workspace == InstallationDiscovery.DefaultWorkspace() && unique.ToolCacheDirectory == InstallationDiscovery.DefaultToolCache(),
            "missing workspace/cache did not receive per-user defaults");
    }

    private static void StatusIsReadOnly()
    {
        using var temp = new Temp();
        var workspace = Path.Combine(temp.Root, "workspace");
        var console = new TestConsole();
        var app = new ConverterApplication(temp.Root, console);
        var options = new ConversionOptions { Workspace = workspace };
        var code = app.RunAsync(new CommandRequest(ConverterCommand.Status, options), CancellationToken.None).GetAwaiter().GetResult();
        Check(code == 0 && console.Output.Contains("Workspace:", StringComparison.Ordinal) &&
              console.Output.Contains("Not completed", StringComparison.Ordinal),
            "status failed without configured game/editor paths or did not display the receipt table");
        Check(!Directory.Exists(workspace), "status created a workspace or modified receipts");
    }

    private static void DoctorExtractIsReadOnly()
    {
        using var temp = new Temp();
        var game = Path.Combine(temp.Root, "game");
        Write(Path.Combine(game, "h3vr_Data/Managed/Assembly-CSharp.dll"), "synthetic marker");
        var workspace = Path.Combine(temp.Root, "workspace");
        var cache = Path.Combine(temp.Root, "cache");
        var previousUnity = Environment.GetEnvironmentVariable("UNITY_EDITOR");
        try
        {
            // If extract-stage doctor performs Unity discovery, this malformed explicit value
            // will surface as an unrelated discovery error.
            Environment.SetEnvironmentVariable("UNITY_EDITOR", Path.Combine(temp.Root, "missing-editor"));
            var console = new TestConsole();
            var app = new ConverterApplication(temp.Root, console);
            var options = new ConversionOptions
            {
                Game = game,
                Workspace = workspace,
                Cache = cache,
                Offline = true,
                Through = "extract"
            };
            var code = app.RunAsync(new CommandRequest(ConverterCommand.Doctor, options), CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(code != 0 && console.Output.Contains("AssetRipper:", StringComparison.Ordinal) &&
                  console.Output.Contains("read-only", StringComparison.OrdinalIgnoreCase),
                "offline doctor did not report tool readiness and its read-only behavior");
            Check(!Directory.Exists(workspace) && !Directory.Exists(cache),
                "offline doctor created a workspace or tool cache while checking availability");
        }
        finally { Environment.SetEnvironmentVariable("UNITY_EDITOR", previousUnity); }
    }

    private static void ExtractNeedsNoEditor()
    {
        using var temp = new Temp();
        var game = Path.Combine(temp.Root, "game");
        Write(Path.Combine(game, "h3vr_Data/Managed/Assembly-CSharp.dll"), "synthetic marker");
        var config = new ConversionConfig
        {
            GameDirectory = game,
            UnityEditor = "",
            Workspace = Path.Combine(temp.Root, "workspace"),
            BuildTarget = "StandaloneOSX"
        };
        var repository = Path.Combine(temp.Root, "repository"); Directory.CreateDirectory(repository);
        config.Validate(repository, "extract");
        Check(Capture(() => config.Validate(repository, "validate")) is InvalidDataException,
            "non-extract validation unexpectedly accepted a missing Unity editor");
    }

    private static void CacheSettingsDoNotInvalidateReceipt()
    {
        using var temp = new Temp();
        var repo = Path.Combine(temp.Root, "repo");
        foreach (var directory in new[] { "src", "tools", "editor", "runtime" })
        {
            Directory.CreateDirectory(Path.Combine(repo, directory));
            Write(Path.Combine(repo, directory, "Fixture.cs"), "// fingerprint fixture\n");
        }
        Write(Path.Combine(repo, "Directory.Build.props"), "<Project />\n");
        Write(Path.Combine(repo, "global.json"), "{}\n");
        var game = Path.Combine(temp.Root, "game");
        Write(Path.Combine(game, "h3vr_Data/Managed/Assembly-CSharp.dll"), "game managed input");
        var recovered = Path.Combine(temp.Root, "recovered");
        Write(Path.Combine(recovered, "ProjectSettings/ProjectVersion.txt"), "m_EditorVersion: 2020.3.1f1\n");
        Write(Path.Combine(recovered, "Assets/source.txt"), "recovered source");
        var workspace = Path.Combine(temp.Root, "workspace");
        var first = new ConversionConfig
        {
            GameDirectory = game,
            UnityEditor = "",
            Workspace = workspace,
            RecoveredProject = recovered,
            ToolCacheDirectory = Path.Combine(temp.Root, "cache-a"),
            Offline = false
        };
        new Pipeline(repo, first).Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        var second = first with { ToolCacheDirectory = Path.Combine(temp.Root, "cache-b"), Offline = true };
        new Pipeline(repo, second).Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        Check(File.Exists(Path.Combine(workspace, ".conversion/extract.json")), "receipt disappeared when cache-only settings changed");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
    }
    private static Exception? Capture(Action action)
    {
        try { action(); return null; } catch (Exception e) { return e; }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "h3vr-application-tests-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
