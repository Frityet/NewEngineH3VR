using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using H3VR.Converter;

namespace UnityMetalCli;

internal static class RendererPackage
{
    private const string Receipt = "Contents/Resources/nativeh3vr-build.json";

    private static string Profile(string app, string renderer) => Path.Combine(app, "Contents", "Helpers", renderer switch
    {
        "base" => "Base.bundle",
        "builtin" => "BuiltIn.bundle",
        "urp" => "URP.bundle",
        _ => throw new ArgumentException("Choose --renderer base, builtin or urp.")
    });

    private static string? DefaultRenderer(string app)
    {
        if (Directory.Exists(Profile(app, "base"))) return "base";
        return Directory.Exists(Profile(app, "urp")) ? "urp" : null;
    }

    internal static (string Path, string? Renderer) Select(string app, string? renderer)
    {
        string? defaultRenderer = DefaultRenderer(app);
        if (defaultRenderer is null)
        {
            if (renderer is not null) throw new ArgumentException("--renderer requires a NativeH3VR app with renderer profiles.");
            return (app, null);
        }
        renderer ??= defaultRenderer;
        string profile = Profile(app, renderer);
        if (!Directory.Exists(profile) || SafeFiles.Canonical(profile) != profile)
            throw new InvalidDataException("The requested renderer is missing or linked outside its expected layout: " + profile);
        return (profile, renderer);
    }

    internal static Task CombineAsync(string original, string builtin, string urp, string output, bool shareAssets,
        CancellationToken cancellation) =>
        PackageAsync([("base", original), ("builtin", builtin), ("urp", urp)], output, shareAssets, cancellation);

    internal static Task PackageUrpAsync(string app, string output, CancellationToken cancellation)
    {
        app = SafeFiles.Canonical(app);
        string source = DefaultRenderer(app) is null ? app : Select(app, "urp").Path;
        return PackageAsync([("urp", source)], output, shareAssets: false, cancellation);
    }

    private static async Task PackageAsync((string Mode, string Path)[] inputs, string output, bool shareAssets,
        CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Packaging macOS players requires macOS.");
        string repository = IntegrationBuild.SourceRoot();
        SafeFiles.RequirePrivateOutput(repository, output);
        output = SafeFiles.Canonical(output);
        if (!output.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Path.Exists(output))
            throw new IOException("Choose a new .app output path.");

        var sources = inputs.Select(source => (source.Mode, Path: SafeFiles.Canonical(source.Path))).ToArray();
        if (sources.Select(source => source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("Supply three distinct renderer builds.");

        string? executable = null;
        (string? Unity, string? Steam)? build = null;
        foreach (var (mode, app) in sources)
        {
            SafeFiles.RequireSeparate(app, output);
            if (DefaultRenderer(app) is not null) throw new ArgumentException("Use an individual player for each renderer.");
            var player = await PlayerRun.VerifyPlayerAsync(app, cancellation);
            if (!player.Stock || !player.SupportsExternalMods)
                throw new InvalidDataException("Expected a signed imported player with schema 2 mod support: " + app);

            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, Receipt)));
            JsonElement metadata = receipt.RootElement;
            var current = (metadata.GetProperty("unityVersion").GetString(), metadata.GetProperty("steamBuildId").GetString());
            if (build.HasValue && build.Value != current)
                throw new InvalidDataException("The renderers must use the same Unity and Steam versions.");
            build = current;
            string? pipeline = metadata.TryGetProperty("graphicsPipeline", out var property) ? property.GetString() : null;
            bool matches = mode == "urp" ? pipeline?.StartsWith("URP ", StringComparison.Ordinal) == true
                : mode == "builtin" ? pipeline == "Built-in" : pipeline is null or "Built-in";
            if (!matches) throw new InvalidDataException($"The build for {mode} has unexpected graphics pipeline: {pipeline}");
            executable ??= Path.GetFileName(player.Program);
        }

        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".combine-" + Guid.NewGuid().ToString("N"));
        string appPath = Path.Combine(stage, Path.GetFileName(output));
        try
        {
            // Preserve the original signed profiles and receipts for existing mods. Clone identical
            // files from one profile so APFS can share their blocks without sharing writable inodes.
            var copies = new Dictionary<(string Relative, long Size), List<string>>();
            long matching = 0;
            foreach (var (mode, source) in sources)
                matching += CopyProfile(source, Profile(appPath, mode), copies, cancellation);

            if (shareAssets)
            {
                string unity = build?.Unity ?? throw new InvalidDataException("The player receipts must identify their Unity version.");
                if (!unity.StartsWith("6000.7.", StringComparison.Ordinal))
                    throw new InvalidDataException("Sharing Unity archives requires Unity 6.7 players.");
                string[] profiles = sources.Select(source => Profile(appPath, source.Mode)).ToArray();
                string[] data = profiles.Select(profile => Path.Combine(profile, "Contents", "Resources", "Data")).ToArray();
                long saved = await SharedUnityArchives.RepackAsync(data, unity, Path.Combine(stage, "asset-sharing"), cancellation);
                foreach (var profile in profiles)
                    await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--force", "--sign", "-", profile], cancellation);
                Console.WriteLine($"Shared Unity archives use {saved:N0} fewer bytes on APFS.");
            }

            string contents = Path.Combine(appPath, "Contents");
            string resources = Path.Combine(contents, "Resources");
            string picker = Path.Combine(contents, "MacOS", executable!);
            string defaultProfile = Profile(appPath, sources[0].Mode);
            Directory.CreateDirectory(resources);
            Directory.CreateDirectory(Path.GetDirectoryName(picker)!);
            SafeFiles.Copy(Path.Combine(defaultProfile, Receipt), Path.Combine(appPath, Receipt));
            Directory.CreateSymbolicLink(Path.Combine(resources, "Data"), Path.GetRelativePath(resources,
                Path.Combine(defaultProfile, "Contents", "Resources", "Data")));
            WriteInfo(Path.Combine(contents, "Info.plist"), executable!);
            await InstallAndSignLauncherAsync(repository, appPath, executable!, cancellation);
            Directory.Move(appPath, output);
            Console.WriteLine(sources.Length == 1 ? $"Packaged the URP player at {output}."
                : $"Combined Base, Built-in and URP at {output}. Reused {matching:N0} bytes of identical files.");
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    // The caller owns the private clone and swaps it into place after this succeeds.
    internal static async Task RefreshLauncherAsync(string app, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Refreshing macOS launchers requires macOS.");
        string repository = IntegrationBuild.SourceRoot();
        SafeFiles.RequirePrivateOutput(repository, app);
        app = SafeFiles.Canonical(app);
        string? defaultRenderer = DefaultRenderer(app);
        if (!Directory.Exists(app) || !app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || defaultRenderer is null)
            throw new InvalidDataException("Supply a staged NativeH3VR app with renderer profiles.");

        string[] renderers = defaultRenderer == "base" ? ["base", "builtin", "urp"] : ["urp"];
        foreach (string renderer in renderers)
        {
            var player = await PlayerRun.VerifyPlayerAsync(Select(app, renderer).Path, cancellation);
            if (!player.Stock || !player.SupportsExternalMods)
                throw new InvalidDataException("Expected a signed imported player with schema 2 mod support: " + renderer);
        }

        string plist = Path.Combine(app, "Contents", "Info.plist");
        string executable = (await ChildProcess.CheckedAsync("/usr/bin/plutil",
            ["-extract", "CFBundleExecutable", "raw", "-o", "-", plist], cancellation, captureOutput: true)).TrimEnd('\r', '\n');
        if (executable.Length == 0 || executable is "." or ".." || executable.Any(char.IsControl) ||
            Path.GetFileName(executable) != executable)
            throw new InvalidDataException("Invalid launcher executable name.");

        await InstallAndSignLauncherAsync(repository, app, executable, cancellation);
        Console.WriteLine($"Refreshed and verified the launcher at {app}.");
    }

    private static async Task InstallAndSignLauncherAsync(string repository, string app, string executable,
        CancellationToken cancellation)
    {
        string contents = Path.Combine(app, "Contents");
        string resources = Path.Combine(contents, "Resources");
        string picker = Path.Combine(contents, "MacOS", executable);
        string launcher = Path.Combine(resources, "Launcher");
        if (SafeFiles.Canonical(launcher) != launcher || SafeFiles.Canonical(picker) != picker ||
            SafeFiles.Canonical(Path.Combine(resources, "dotnet-path.txt")) != Path.Combine(resources, "dotnet-path.txt"))
            throw new InvalidDataException("Launcher paths must not be symbolic links.");
        string compiled = picker + "." + Guid.NewGuid().ToString("N");
        try
        {
            await ChildProcess.CheckedAsync("/usr/bin/xcrun", ["swiftc", "-O", "-parse-as-library", "-target", "arm64-apple-macos12",
                Path.Combine(repository, "tools", "UnityMetalCli", "RendererPicker.swift"), "-o", compiled], cancellation);
            await IntegrationBuild.RequireArm64Async(compiled, cancellation);
            InstallLauncher(resources);
            File.Move(compiled, picker, overwrite: true);
            await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--force", "--sign", "-", app], cancellation);
            await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], cancellation);
        }
        finally
        {
            File.Delete(compiled);
        }
    }

    private static long CopyProfile(string source, string output,
        Dictionary<(string Relative, long Size), List<string>> copies, CancellationToken cancellation)
    {
        long matching = 0;
        foreach (string file in SafeFiles.Files(source))
        {
            cancellation.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            long size = new FileInfo(file).Length;
            string target = Path.Combine(output, relative);
            var key = (relative, size);
            if (!copies.TryGetValue(key, out var candidates)) copies.Add(key, candidates = []);
            string? hash = candidates.Count == 0 ? null : SafeFiles.Hash(file);
            string? match = candidates.FirstOrDefault(candidate =>
                (!OperatingSystem.IsMacOS() || File.GetUnixFileMode(candidate) == File.GetUnixFileMode(file)) &&
                SafeFiles.Hash(candidate) == hash);
            SafeFiles.Copy(match ?? file, target);
            if (match is null) candidates.Add(target);
            else matching += size;
        }
        return matching;
    }

    private static void WriteInfo(string path, string executable)
    {
        object Entry(string key, string value) => new[] { new XElement("key", key), new XElement("string", value) };
        var document = new XDocument(new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
            "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                Entry("CFBundleExecutable", executable), Entry("CFBundleIdentifier", "local.nativeh3vr.launcher"),
                Entry("CFBundleName", "NativeH3VR"), Entry("CFBundlePackageType", "APPL"),
                Entry("CFBundleVersion", "1"), Entry("LSMinimumSystemVersion", "12.0"),
                new XElement("key", "LSUIElement"), new XElement("true"))));
        document.Save(path);
    }

    private static void InstallLauncher(string resources)
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "dotnet"));
        string dotnet = candidates.FirstOrDefault(File.Exists) ??
            Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        if (!File.Exists(dotnet)) throw new FileNotFoundException("Could not find the .NET host for the app launcher.", dotnet);
        string dotnetPath = Path.Combine(resources, "dotnet-path.txt");
        string launcher = Path.Combine(resources, "Launcher");
        Directory.CreateDirectory(launcher);
        string temporary = Path.Combine(launcher, Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, dotnet + "\n");
            File.Move(temporary, dotnetPath, overwrite: true);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".dll", StringComparison.Ordinal) || name is "UnityMetalCli.deps.json" or "UnityMetalCli.runtimeconfig.json")
                {
                    SafeFiles.Copy(file, temporary);
                    File.Move(temporary, Path.Combine(launcher, name), overwrite: true);
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
