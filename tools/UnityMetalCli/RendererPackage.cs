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

    internal static string Select(string app, string? renderer)
    {
        if (!Directory.Exists(Profile(app, "base")))
        {
            if (renderer is not null) throw new ArgumentException("--renderer requires a combined NativeH3VR app.");
            return app;
        }
        string profile = Profile(app, renderer ?? "base");
        if (!Directory.Exists(profile) || SafeFiles.Canonical(profile) != profile)
            throw new InvalidDataException("The requested renderer is missing or linked outside its expected layout: " + profile);
        return profile;
    }

    internal static async Task CombineAsync(string original, string builtin, string urp, string output, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Combining macOS players requires macOS.");
        string repository = IntegrationBuild.SourceRoot();
        SafeFiles.RequirePrivateOutput(repository, output);
        output = SafeFiles.Canonical(output);
        if (!output.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Path.Exists(output))
            throw new IOException("Choose a new .app output path.");

        var sources = new[] { (Mode: "base", Path: original), (Mode: "builtin", Path: builtin), (Mode: "urp", Path: urp) }
            .Select(source => (source.Mode, Path: SafeFiles.Canonical(source.Path))).ToArray();
        if (sources.Select(source => source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("Supply three distinct renderer builds.");

        string? executable = null;
        (string? Unity, string? Steam)? build = null;
        foreach (var (mode, app) in sources)
        {
            SafeFiles.RequireSeparate(app, output);
            if (Select(app, null) != app) throw new ArgumentException("Use individual builds as combine inputs.");
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

            string contents = Path.Combine(appPath, "Contents");
            string resources = Path.Combine(contents, "Resources");
            string picker = Path.Combine(contents, "MacOS", executable!);
            string baseProfile = Profile(appPath, "base");
            Directory.CreateDirectory(resources);
            Directory.CreateDirectory(Path.GetDirectoryName(picker)!);
            SafeFiles.Copy(Path.Combine(baseProfile, Receipt), Path.Combine(appPath, Receipt));
            Directory.CreateSymbolicLink(Path.Combine(resources, "Data"), Path.GetRelativePath(resources,
                Path.Combine(baseProfile, "Contents", "Resources", "Data")));
            WriteInfo(Path.Combine(contents, "Info.plist"), executable!);
            InstallLauncher(resources);
            await ChildProcess.CheckedAsync("/usr/bin/xcrun", ["swiftc", "-O", "-parse-as-library", "-target", "arm64-apple-macos12",
                Path.Combine(repository, "tools", "UnityMetalCli", "RendererPicker.swift"), "-o", picker], cancellation);
            await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--force", "--sign", "-", appPath], cancellation);
            await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", appPath], cancellation);
            Directory.Move(appPath, output);
            Console.WriteLine($"Combined Base, Built-in and URP at {output}. Reused {matching:N0} bytes of identical files.");
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
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
        File.WriteAllText(Path.Combine(resources, "dotnet-path.txt"), dotnet + "\n");
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".dll", StringComparison.Ordinal) || name is "UnityMetalCli.deps.json" or "UnityMetalCli.runtimeconfig.json")
                SafeFiles.Copy(file, Path.Combine(resources, "Launcher", name));
        }
    }
}
