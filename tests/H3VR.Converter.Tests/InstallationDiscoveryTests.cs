using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class InstallationDiscoveryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Steam library discovery follows escaped multi-library VDF paths and filters H3VR", SteamLibraries);
        yield return ("Unity discovery returns only the pinned Hub version and explicit editor", UnityEditors);
        yield return ("Unity editor path normalization accepts executable and install folders", EditorNormalization);
        yield return ("default converter paths are stable per-user locations", DefaultPaths);
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
        var other = Path.Combine(hub, "2022.3.0f1");
        var explicitTarget = Path.Combine(temp.Root, "custom", InstallationDiscovery.TargetUnityVersion);
        var targetExe = InstallEditor(target);
        InstallEditor(other);
        InstallEditor(explicitTarget);
        var found = InstallationDiscovery.DiscoverUnityEditors(temp.Root, [hub], explicitTarget);
        Check(found.Count == 2 && found.Contains(targetExe) && found.Contains(InstallationDiscovery.NormalizeEditorPath(explicitTarget)),
            "Unity discovery failed to return Hub plus explicit target editors or returned a non-target version");
    }

    private static void EditorNormalization()
    {
        using var temp = new Temp();
        var install = Path.Combine(temp.Root, "2021.3.45f2");
        var executable = InstallEditor(install);
        Check(InstallationDiscovery.NormalizeEditorPath(executable) == executable, "Unity executable path changed during normalization");
        Check(InstallationDiscovery.NormalizeEditorPath(install) == executable, "installed Unity editor folder did not normalize to its executable");
        var invalid = Path.Combine(temp.Root, "not-unity"); Directory.CreateDirectory(invalid);
        Check(Capture(() => InstallationDiscovery.NormalizeEditorPath(invalid)) is InvalidDataException,
            "folder without a Unity executable was accepted");
    }

    private static void DefaultPaths()
    {
        var home = Path.Combine(Path.GetTempPath(), "unity-user-home");
        Check(InstallationDiscovery.DefaultWorkspace(home) == Path.Combine(home, "H3VR-Unity2021"), "workspace default is not user-local");
        Check(InstallationDiscovery.DefaultToolCache(home) == Path.Combine(home, ".cache", "h3vr-converter"), "tool cache default is not a persistent per-user cache");
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
