using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class InstallationDiscoveryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Steam library discovery follows escaped multi-library VDF paths and filters H3VR", SteamLibraries);
        yield return ("Unity discovery filters to the exact selected Unity 6.7 patch", UnityEditors);
        yield return ("Unity editor path normalization accepts executable and install folders", EditorNormalization);
        yield return ("Unity 6.7 editor layout uses relocated script and Mono reference assemblies", EditorLayout);
        yield return ("Unity version guard accepts only exact 6.7 editor versions", SupportedVersions);
        yield return ("default conversion workspace is isolated under the checkout", DefaultWorkspace);
    }

    private static void SteamLibraries()
    {
        using var temp = new Temp();
        var main = Path.Combine(temp.Root, "Steam Root");
        var second = Path.Combine(temp.Root, "Library With Spaces");
        InstallGame(main, "main"); InstallGame(second, "extra");
        Write(Path.Combine(main, "steamapps/libraryfolders.vdf"), """
            "libraryfolders"
            {
              "0" { "path" "C:\\Steam Root" }
              "1" { "path" "D:\\Library With Spaces" }
            }
            """);
        // The parser is also exercised with native paths containing whitespace so the
        // directory discovery and VDF decoding are independent of host OS path syntax.
        var vdf = "\"libraryfolders\" { \"0\" { \"path\" \"" + EscapeVdfPath(main) + "\" } \"1\" { \"path\" \"" + EscapeVdfPath(second) + "\" } }";
        Write(Path.Combine(main, "steamapps/libraryfolders.vdf"), vdf);
        var found = InstallationDiscovery.DiscoverGameDirectories(temp.Root, [main]);
        Check(found.Count == 2 && found.Contains(Path.Combine(main, "steamapps/common/H3VR")) && found.Contains(Path.Combine(second, "steamapps/common/H3VR")),
            "Steam libraryfolders.vdf did not resolve all H3VR libraries with spaces");
        var decoded = InstallationDiscovery.ParseSteamLibraries("\"path\" \"D:\\\\Steam Library\\\\Games\"");
        Check(decoded.Single() == "D:\\Steam Library\\Games", "VDF escaped Windows separators were not decoded consistently");
        Check(InstallationDiscovery.ParseSteamLibraries("\"libraryfolders\" { \"0\" \"D:\\\\Legacy Steam\" }").Single() == "D:\\Legacy Steam",
            "legacy Steam libraryfolders VDF entries were not parsed");
    }

    private static void UnityEditors()
    {
        using var temp = new Temp();
        var hub = Path.Combine(temp.Root, "Hub Editors");
        var target = Path.Combine(hub, InstallationDiscovery.TargetUnityVersion);
        const string alternate = "6000.7.0b9";
        var otherPatch = Path.Combine(hub, alternate);
        var outsideSeries = Path.Combine(hub, "6000.6.9f1");
        var explicitTarget = Path.Combine(temp.Root, "custom", InstallationDiscovery.TargetUnityVersion);
        var targetExe = InstallEditor(target);
        var otherPatchExe = InstallEditor(otherPatch);
        InstallEditor(outsideSeries);
        InstallEditor(explicitTarget);
        var found = InstallationDiscovery.DiscoverUnityEditors(temp.Root, [hub], explicitTarget);
        Check(found.Count == 2 && found.Contains(targetExe) && found.Contains(InstallationDiscovery.NormalizeEditorPath(explicitTarget)),
            "Unity discovery failed to return Hub plus explicit target editors or returned a non-target version");
        Check(InstallationDiscovery.DiscoverUnityEditors(temp.Root, [hub], explicitTarget, alternate)
            .SequenceEqual(new[] { otherPatchExe }), "custom patch selection included the default patch");
        Check(Capture(() => InstallationDiscovery.DiscoverUnityEditors(temp.Root, [hub], explicitTarget, "6000.6.9f1")) is InvalidDataException,
            "direct discovery accepted an editor outside Unity 6.7");
    }

    private static void EditorNormalization()
    {
        using var temp = new Temp();
        var install = Path.Combine(temp.Root, ConversionConfig.TargetVersion);
        var executable = InstallEditor(install);
        Check(InstallationDiscovery.NormalizeEditorPath(executable) == executable, "Unity executable path changed during normalization");
        Check(InstallationDiscovery.NormalizeEditorPath(install) == executable, "installed Unity editor folder did not normalize to its executable");
        var invalid = Path.Combine(temp.Root, "not-unity"); Directory.CreateDirectory(invalid);
        Check(Capture(() => InstallationDiscovery.NormalizeEditorPath(invalid)) is InvalidDataException,
            "folder without a Unity executable was accepted");
    }

    private static void EditorLayout()
    {
        using var temp = new Temp();
        var install = Path.Combine(temp.Root, ConversionConfig.TargetVersion);
        var data = OperatingSystem.IsMacOS()
            ? Path.Combine(install, "Unity.app", "Contents")
            : Path.Combine(install, "Editor", "Data");
        var executable = OperatingSystem.IsMacOS()
            ? Path.Combine(data, "MacOS", "Unity") : Path.Combine(install, "Editor", "Unity.exe");
        var config = new ConversionConfig { UnityEditor = executable };
        var scripting = Path.Combine(data, "Resources", "Scripting");
        Check(config.UnityData == data && config.EngineAssemblies == Path.Combine(scripting, "Managed", "UnityEngine") &&
              config.MonoReferenceAssemblies == Path.Combine(scripting, "MonoBleedingEdge", "lib", "mono", "4.7.1-api"),
            "Unity 6.7 editor scripting assemblies are under Data/Resources/Scripting");
        if (OperatingSystem.IsMacOS())
            Check(config.MacPlayerAssemblies == Path.Combine(install, "PlaybackEngines", "MacStandaloneSupport", "Variations", "mono", "Managed"),
                "Unity 6.7 macOS playback support is alongside Unity.app");

        Write(Path.Combine(data, "Info.plist"), $"<plist><dict><key>CFBundleVersion</key><string>{ConversionConfig.TargetVersion}</string></dict></plist>");
        config.VerifyEditorVersion();
        Check(Capture(() => (config with { UnityVersion = "6000.7.0b9" }).VerifyEditorVersion()) is InvalidDataException,
            "a different 6.7 patch must fail the configured version check");
    }

    private static void SupportedVersions()
    {
        foreach (var version in new[] { "6000.7.0a1", "6000.7.0b10", "6000.7.1f1", "6000.7.1p1" })
            ConversionConfig.RequireSupportedUnityVersion(version);
        foreach (var version in new[] { "6.7", "6000.7.0", "6000.6.9f1", "6000.70.0b1", "6000.7.0b10\n" })
            Check(Capture(() => ConversionConfig.RequireSupportedUnityVersion(version)) is InvalidDataException,
                "accepted a version outside the exact 6.7 series: " + version);
    }

    private static void DefaultWorkspace()
    {
        var checkout = Path.Combine(Path.GetTempPath(), "synthetic-checkout");
        Check(InstallationDiscovery.DefaultWorkspace(checkout) == Path.GetFullPath(Path.Combine(checkout, ".local", "workspace")),
            "workspace default is not rooted in the supplied checkout's .local folder");
    }

    private static void InstallGame(string library, string marker)
    {
        Write(Path.Combine(library, "steamapps/common/H3VR/h3vr_Data/Managed/Assembly-CSharp.dll"), marker);
        Write(Path.Combine(library, "steamapps/common/OtherGame/h3vr_Data/Managed/Assembly-CSharp.dll"), "not H3VR");
    }

    private static string InstallEditor(string versionDirectory)
    {
        var executable = Path.Combine(versionDirectory, "Editor/Unity.exe");
        Write(executable, "synthetic unity executable");
        return executable;
    }

    private static string EscapeVdfPath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "h3vr-discovery-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
