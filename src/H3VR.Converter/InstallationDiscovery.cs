using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace H3VR.Converter;

/// <summary>Bounded discovery of standard Steam and Unity Hub installations.</summary>
public static class InstallationDiscovery
{
    public const string TargetUnityVersion = "2021.3.45f2";

    public static string DefaultWorkspace(string? homeDirectory = null) =>
        Path.Combine(ResolveHome(homeDirectory), "H3VR-Unity2021");

    public static string DefaultToolCache(string? homeDirectory = null) =>
        Path.Combine(ResolveHome(homeDirectory), ".cache", "h3vr-converter");

    public static IReadOnlyList<string> DiscoverGameDirectories(string? homeDirectory = null, IEnumerable<string>? steamRoots = null)
    {
        var roots = (steamRoots ?? StandardSteamRoots(ResolveHome(homeDirectory))).Select(Path.GetFullPath).Distinct(PathComparer()).ToList();
        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            foreach (var library in ParseSteamLibraries(File.ReadAllText(libraryFile)))
                libraries.Add(Path.IsPathRooted(library) ? library : Path.Combine(root, library));
        }

        var results = new List<string>();
        foreach (var library in libraries.Distinct(PathComparer()))
        {
            var game = Path.Combine(library, "steamapps", "common", "H3VR");
            if (File.Exists(Path.Combine(game, "h3vr_Data", "Managed", "Assembly-CSharp.dll"))) results.Add(Path.GetFullPath(game));
        }
        return results.Distinct(PathComparer()).Order(PathComparer()).ToArray();
    }

    /// <summary>Returns only matching target editors found in Unity Hub roots or UNITY_EDITOR.</summary>
    public static IReadOnlyList<string> DiscoverUnityEditors(string? homeDirectory = null,
        IEnumerable<string>? hubEditorRoots = null, string? unityEditorEnvironment = null)
    {
        var roots = (hubEditorRoots ?? StandardUnityHubRoots(ResolveHome(homeDirectory)))
            .Select(Path.GetFullPath).Distinct(PathComparer()).ToList();
        var candidates = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var versionDirectory in Directory.EnumerateDirectories(root))
            {
                if (!Path.GetFileName(versionDirectory).Equals(TargetUnityVersion, StringComparison.OrdinalIgnoreCase)) continue;
                try { candidates.Add(NormalizeEditorPath(versionDirectory)); }
                catch (InvalidDataException) { }
            }
        }

        unityEditorEnvironment ??= Environment.GetEnvironmentVariable("UNITY_EDITOR");
        if (!string.IsNullOrWhiteSpace(unityEditorEnvironment))
        {
            var explicitEditor = NormalizeEditorPath(unityEditorEnvironment);
            if (IsTargetEditor(explicitEditor)) candidates.Add(explicitEditor);
        }
        return candidates.Distinct(PathComparer()).Order(PathComparer()).ToArray();
    }

    /// <summary>Accepts a Unity executable, macOS Unity.app, or installed editor/version folder.</summary>
    public static string NormalizeEditorPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Unity editor path is empty.");
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        if (File.Exists(full))
        {
            if (!Path.GetFileName(full).Equals("Unity", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(full).Equals("Unity.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UnityEditor must point to the Unity executable: " + full);
            return full;
        }
        if (!Directory.Exists(full)) throw new FileNotFoundException("Unity editor path does not exist.", full);

        var candidates = new[]
        {
            Path.Combine(full, "Contents", "MacOS", "Unity"),
            Path.Combine(full, "MacOS", "Unity"),
            Path.Combine(full, "Unity.app", "Contents", "MacOS", "Unity"),
            Path.Combine(full, "Editor", "Unity.exe"),
            Path.Combine(full, "Editor", "Unity"),
            Path.Combine(full, "Unity.exe"),
            Path.Combine(full, "Unity")
        }.Where(File.Exists).Distinct(PathComparer()).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1) throw new InvalidDataException("Unity editor folder contains multiple executable candidates; pass the exact executable path.");
        throw new InvalidDataException("Could not find a Unity executable in the supplied editor folder: " + full);
    }

    internal static IReadOnlyList<string> ParseSteamLibraries(string vdf)
    {
        var tokens = TokenizeVdf(vdf);
        var libraries = new List<string>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var isPathField = tokens[i].Equals("path", StringComparison.OrdinalIgnoreCase);
            var isLegacyIndex = int.TryParse(tokens[i], out _);
            if ((isPathField || isLegacyIndex) && tokens[i + 1] is not "{" and not "}")
                libraries.Add(tokens[i + 1]);
        }
        return libraries.Distinct(PathComparer()).ToArray();
    }

    private static List<string> TokenizeVdf(string text)
    {
        var tokens = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2; while (i < text.Length && text[i] is not '\r' and not '\n') i++; continue;
            }
            if (text[i] is '{' or '}') { tokens.Add(text[i++].ToString()); continue; }
            if (text[i] != '"') { i++; continue; }
            i++; var value = new StringBuilder();
            while (i < text.Length)
            {
                var ch = text[i++];
                if (ch == '"') break;
                if (ch == '\\' && i < text.Length)
                {
                    var escaped = text[i++];
                    value.Append(escaped switch { '"' => '"', '\\' => '\\', 'n' => '\n', 't' => '\t', _ => '\\' }).Append(escaped is '"' or '\\' or 'n' or 't' ? string.Empty : escaped.ToString());
                }
                else value.Append(ch);
            }
            tokens.Add(value.ToString());
        }
        return tokens;
    }

    private static bool IsTargetEditor(string executable)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!); directory is not null; directory = directory.Parent)
            if (directory.Name.Equals(TargetUnityVersion, StringComparison.OrdinalIgnoreCase)) return true;

        var plist = OperatingSystem.IsMacOS()
            ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!, "Info.plist")
            : Path.Combine(Path.GetDirectoryName(executable)!, "Data", "Info.plist");
        if (File.Exists(plist))
        {
            try
            {
                var xml = XDocument.Load(plist);
                var key = xml.Descendants("key").FirstOrDefault(x => x.Value == "CFBundleVersion");
                if (key?.ElementsAfterSelf().FirstOrDefault()?.Value == TargetUnityVersion) return true;
            }
            catch (System.Xml.XmlException) { }
        }
        try { return FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Contains(TargetUnityVersion, StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception) { return false; }
    }

    private static IEnumerable<string> StandardSteamRoots(string home)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library/Application Support/Steam");
            yield return Path.Combine(home, "Library/Steam");
        }
        else if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFiles64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "Steam");
            if (!string.IsNullOrEmpty(programFiles64)) yield return Path.Combine(programFiles64, "Steam");
            yield return Path.Combine(home, "Steam");
        }
        else
        {
            yield return Path.Combine(home, ".steam/steam");
            yield return Path.Combine(home, ".local/share/Steam");
        }
    }

    private static IEnumerable<string> StandardUnityHubRoots(string home)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library/Application Support/UnityHub/Editors");
            yield return "/Applications/Unity/Hub/Editor";
        }
        else if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "Unity/Hub/Editor");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Programs/Unity/Hub/Editor");
        }
        else
        {
            yield return Path.Combine(home, "Unity/Hub/Editor");
            yield return "/opt/Unity/Hub/Editor";
        }
    }

    private static string ResolveHome(string? home) => Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
