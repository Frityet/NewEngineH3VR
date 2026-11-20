using System.Diagnostics;
using System.Text.Json;

namespace H3VR.Converter;

internal static class LocalPlatformPreparation
{
    private const string ReceiptName = "platform-integration-receipt.json";
    private const string ManifestName = "integration.json";
    private const string NativeBuilderProject = "tools/PlatformPatch/NativeBuilder/NativeBuilder.csproj";
    private const string UnityMetalProject = "tools/UnityMetalCli/UnityMetalCli.csproj";
    private const string SteamHeadersDirectory = ".local/sdk/steamworks-1.41/public/steam";
    private const string FmodSdkDirectory = ".local/sdk/fmod-2.03.15";

    private static readonly string[] FmodSdkFiles =
    [
        "api/core/inc/fmod_common.h", "api/core/lib/libfmod.dylib",
        "api/studio/inc/fmod_studio.h", "api/studio/lib/libfmodstudio.dylib"
    ];

    private sealed record Inputs(string Wrapper, string SteamHeaders, string SteamApi, string FmodSdk,
        string OpenXrHeaders, string UnityHeaders);
    private sealed record CacheReceipt(int SchemaVersion, string Fingerprint, Dictionary<string, string> Files);
    private sealed record PublicSource(string Name, string Remote, string Revision, string InputDirectory,
        string[] BuildInputs, string BuildInputsHash);

    private static readonly PublicSource CSteamworks = new("csteamworks", "https://github.com/rlabrecque/CSteamworks.git",
        "407dcba0151cfa52d1814a752e3038f16dbedc4d", "",
        ["src", "CSteamworks2.py", "SteamworksParser/__init__.py", "SteamworksParser/steamworksparser.py"],
        "f8a28d6b337ae9403f9e3c72ec0ce4f33d0d6001eecaf925bd9fd34a1423b7ba");
    private static readonly PublicSource OpenXr = new("openxr-sdk", "https://github.com/KhronosGroup/OpenXR-SDK.git",
        "f2448a8797c85814aa892efc1ab8707900fbcc78", "include", ["include/openxr"],
        "1b1fff23713901b671ff9ef3b6b94a7b5622735bf1d713991c29f9d824f5ce76");

    internal static void Validate(string repository, ConversionConfig config) => _ = ValidateInputs(repository, WithEnvironment(config));

    internal static void ValidateLicensedSdkInputs(string repository, ConversionConfig config) => _ = LicensedSdks(repository, WithEnvironment(config));

    internal static async Task<string> PrepareAsync(string repository, ConversionConfig config, CancellationToken cancellation)
    {
        config = WithEnvironment(config);
        _ = ValidateInputs(repository, config);
        if (config.CSteamworks is null) await ProvisionAsync(repository, config, CSteamworks, cancellation);
        if (config.OpenXrHeaders is null) await ProvisionAsync(repository, config, OpenXr, cancellation);
        var inputs = ValidateInputs(repository, config, requirePublicSources: true);
        string fingerprint = await FingerprintAsync(repository, config.UnityVersion, inputs, cancellation);
        string cache = CacheDirectory(repository);
        string output = Path.Combine(cache, fingerprint);
        SafeFiles.RequirePrivateOutput(repository, output);
        if (Directory.Exists(output))
        {
            string manifest = Verify(output, fingerprint);
            Console.WriteLine("Reusing verified macOS platform integration: " + manifest);
            return manifest;
        }
        if (File.Exists(output)) throw new IOException("Platform integration cache is occupied by a file: " + output);

        string stage = Path.Combine(cache, ".build-" + Guid.NewGuid().ToString("N"));
        SafeFiles.RequirePrivateOutput(repository, stage);
        Directory.CreateDirectory(stage);
        try
        {
            string platform = Path.Combine(stage, "platform");
            Console.WriteLine("Building private macOS platform integration from local SDKs.");
            await RunTool(NativeBuilderProject, ["--csteamworks", inputs.Wrapper,
                "--steamworks-headers", inputs.SteamHeaders, "--steam-api", inputs.SteamApi,
                "--fmod-sdk", inputs.FmodSdk, "--output", platform]);

            string integration = Path.Combine(stage, "integration");
            await RunTool(UnityMetalProject, ["build", "--unity-editor", config.UnityEditor,
                "--openxr-headers", inputs.OpenXrHeaders, "--platform", platform, "--output", integration,
                "--unity-version", config.UnityVersion]);

            var current = ValidateInputs(repository, config, requirePublicSources: true);
            if (await FingerprintAsync(repository, config.UnityVersion, current, cancellation) != fingerprint)
                throw new IOException("Platform SDK, source, or toolchain inputs changed during the build. Rerun the conversion.");
            SafeFiles.AtomicJson(Path.Combine(integration, ReceiptName), new CacheReceipt(1, fingerprint, Inventory(integration)));
            _ = Verify(integration, fingerprint);
            cancellation.ThrowIfCancellationRequested();
            try { Directory.Move(integration, output); }
            catch (IOException) when (Directory.Exists(output))
            {
                // A concurrent build with the same inputs may have published first.
                SafeFiles.RequirePrivateOutput(repository, output);
            }
            return Verify(output, fingerprint);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }

        Task RunTool(string project, string[] arguments)
        {
            string[] restore = config.Offline
                ? ["--property:RestoreSources=" + stage, "--property:NuGetAudit=false"] : [];
            return Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, project), "--configuration", "Release",
                "--artifacts-path", Path.Combine(stage, "dotnet"), .. restore, "--", .. arguments], repository, cancellation: cancellation);
        }
    }

    private static Inputs ValidateInputs(string repository, ConversionConfig config, bool requirePublicSources = false)
    {
        if (!OperatingSystem.IsMacOS() || config.BuildTarget != "StandaloneOSX")
            throw new InvalidDataException("Automatic macOS platform integration requires a macOS host and --target macos.");

        var (steamHeaders, fmod) = LicensedSdks(repository, config);
        string steamApi = Required(config.SteamApi ?? InstallationDiscovery.DiscoverMacSteamApi(), "", "--steam-api",
            "the installed Steam client libsteam_api.dylib");
        string wrapper = config.CSteamworks is { } customWrapper
            ? Required(customWrapper, "src/steam_api.cpp", "--csteamworks", "the pinned CSteamworks checkout")
            : ResolvePublicSource(repository, config, CSteamworks, requirePublicSources);
        if (config.CSteamworks is not null)
        {
            bool hasHeader = File.Exists(Path.Combine(wrapper, "src/CSteamworks.h"));
            bool hasBindings = File.Exists(Path.Combine(wrapper, "wrapper/unitybuild.cpp"));
            bool hasGenerator = File.Exists(Path.Combine(wrapper, "CSteamworks2.py")) &&
                File.Exists(Path.Combine(wrapper, "SteamworksParser/steamworksparser.py"));
            if (!hasHeader || !(hasBindings || hasGenerator))
                throw new InvalidDataException("Supply the pinned CSteamworks checkout with generated bindings or initialize its SteamworksParser submodule.");
        }
        string openXr = config.OpenXrHeaders is { } customHeaders
            ? Required(customHeaders, "openxr/openxr_platform.h", "--openxr-headers", "the Khronos OpenXR SDK include directory with Metal headers")
            : ResolvePublicSource(repository, config, OpenXr, requirePublicSources);
        if (config.OpenXrHeaders is not null && (!File.Exists(Path.Combine(openXr, "openxr/openxr.h")) ||
            !File.ReadAllText(Path.Combine(openXr, "openxr/openxr_platform.h")).Contains("XR_KHR_metal_enable", StringComparison.Ordinal)))
            throw new InvalidDataException("Set --openxr-headers to a Khronos OpenXR SDK include directory with XR_KHR_metal_enable.");
        string unity = Required(Path.Combine(config.UnityData, "Resources", "PluginAPI"), "IUnityGraphicsMetal.h",
            "--unity", "the Unity 6.7 editor with Metal plug-in API headers");

        string cache = CacheDirectory(repository);
        SafeFiles.RequirePrivateOutput(repository, cache);
        string?[] sourceOutputs = [config.CSteamworks is null ? PublicSourceDirectory(repository, config, CSteamworks) : null,
            config.OpenXrHeaders is null ? PublicSourceDirectory(repository, config, OpenXr) : null];
        foreach (string output in sourceOutputs.OfType<string>())
            foreach (string input in new[] { steamHeaders, Path.GetDirectoryName(steamApi)!, fmod, config.CSteamworks, config.OpenXrHeaders }.OfType<string>())
                SafeFiles.RequireSeparate(input, output);
        string[] platformInputs = [wrapper, steamHeaders, Path.GetDirectoryName(steamApi)!, fmod, openXr, config.UnityData];
        foreach (string input in platformInputs)
            if (config.Workspace.Length != 0) SafeFiles.RequireSeparate(input, config.Workspace);
        foreach (string input in platformInputs.Concat(new[] { config.GameDirectory, config.Workspace, config.RecoveredProject, config.ExtractionWorkspace,
            config.ShaderSources }.OfType<string>()).Where(path => path.Length != 0))
            SafeFiles.RequireSeparate(input, cache);
        return new(wrapper, steamHeaders, steamApi, fmod, openXr, unity);

        static string Required(string? path, string marker, string flag, string description)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.Combine(path, marker)))
                throw new InvalidDataException($"Set {flag} to {description}.");
            return SafeFiles.Canonical(path);
        }
    }

    private static ConversionConfig WithEnvironment(ConversionConfig config) => (config with
    {
        CSteamworks = config.CSteamworks ?? Environment.GetEnvironmentVariable("NATIVEH3VR_CSTEAMWORKS"),
        SteamworksHeaders = config.SteamworksHeaders ?? Environment.GetEnvironmentVariable("NATIVEH3VR_STEAMWORKS_HEADERS"),
        SteamApi = config.SteamApi ?? Environment.GetEnvironmentVariable("NATIVEH3VR_STEAM_API"),
        OpenXrHeaders = config.OpenXrHeaders ?? Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR_HEADERS"),
        FmodSdk = config.FmodSdk ?? Environment.GetEnvironmentVariable("NATIVEH3VR_FMOD_SDK")
    }).ResolvePaths(Environment.CurrentDirectory);

    private static (string SteamHeaders, string FmodSdk) LicensedSdks(string repository, ConversionConfig config)
    {
        string? steamOverride = config.SteamworksHeaders;
        string steamDirectory = steamOverride ?? Path.Combine(repository, SteamHeadersDirectory);
        if (steamOverride is null && !File.Exists(Path.Combine(steamDirectory, "steam_api.h")) &&
            config.CSteamworks is { } wrapper && File.Exists(Path.Combine(wrapper, "steam", "steam_api.h")))
            steamDirectory = Path.Combine(wrapper, "steam");
        string fmodDirectory = config.FmodSdk ?? Path.Combine(repository, FmodSdkDirectory);

        var problems = new List<string>();
        if (!File.Exists(Path.Combine(steamDirectory, "steam_api.h")))
            problems.Add($"Steamworks SDK 1.41 public/steam headers are missing from {steamDirectory}. " +
                "Get the SDK from https://partner.steamgames.com/downloads/list and use --steamworks-headers or place it under .local/sdk/steamworks-1.41.");
        string[] missingFmod = FmodSdkFiles.Where(file => !File.Exists(Path.Combine(fmodDirectory, file))).ToArray();
        if (missingFmod.Length != 0)
            problems.Add($"FMOD Engine 2.03.15 for macOS is incomplete at {fmodDirectory}. Missing {string.Join(", ", missingFmod)}. " +
                "Get the SDK from https://www.fmod.com/download and use --fmod-sdk or place it under .local/sdk/fmod-2.03.15.");
        if (problems.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, problems));
        return (SafeFiles.Canonical(steamDirectory), SafeFiles.Canonical(fmodDirectory));
    }

    private static string CacheDirectory(string repository) => Path.Combine(repository, ".local", "platform-integration-cache");

    private static string PublicSourceDirectory(string repository, ConversionConfig config, PublicSource source) =>
        Path.Combine(config.CacheDirectory(repository), "platform-sources", $"{source.Name}-{source.Revision}");

    private static string ResolvePublicSource(string repository, ConversionConfig config, PublicSource source, bool required)
    {
        string directory = PublicSourceDirectory(repository, config, source);
        SafeFiles.RequireToolCachePath(repository, directory);
        if (Directory.Exists(directory)) VerifyPublicSource(repository, directory, source);
        else if (File.Exists(directory)) throw new IOException("Platform source cache is occupied by a file: " + directory);
        else if (required || config.Offline)
            throw new InvalidOperationException($"Offline conversion needs a verified {source.Name} source cache at {directory} or an explicit local path. Run once without --offline to populate it.");
        return Path.Combine(directory, source.InputDirectory);
    }

    private static async Task ProvisionAsync(string repository, ConversionConfig config, PublicSource source, CancellationToken cancellation)
    {
        string directory = PublicSourceDirectory(repository, config, source);
        SafeFiles.RequireToolCachePath(repository, directory);
        if (Directory.Exists(directory)) return;
        if (config.Offline) throw new InvalidOperationException("Offline conversion cannot download " + source.Name);

        string stage = directory + ".build-" + Guid.NewGuid().ToString("N");
        SafeFiles.RequireToolCachePath(repository, stage);
        Directory.CreateDirectory(stage);
        try
        {
            await Git("init", "--quiet", "--template=");
            await Git("config", "core.autocrlf", "false");
            await Git("remote", "add", "origin", source.Remote);
            await Git("fetch", "--depth=1", "--no-tags", "origin", source.Revision);
            await Git("checkout", "--detach", source.Revision);
            if (source == CSteamworks)
                await Git("submodule", "update", "--init", "--depth=1", "--", "SteamworksParser");
            VerifyPublicSource(repository, stage, source);
            cancellation.ThrowIfCancellationRequested();
            try { Directory.Move(stage, directory); }
            catch (IOException) when (Directory.Exists(directory)) { VerifyPublicSource(repository, directory, source); }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }

        Task Git(params string[] arguments) => Processes.Run("git", ["-c", "core.hooksPath=/dev/null", .. arguments], stage, cancellation: cancellation);
    }

    private static void VerifyPublicSource(string repository, string directory, PublicSource source)
    {
        SafeFiles.RequireToolCachePath(repository, Path.Combine(directory, ".git"));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string relative in source.BuildInputs)
        {
            string path = Path.Combine(directory, relative);
            SafeFiles.RequireToolCachePath(repository, path);
            if (Directory.Exists(path))
                foreach (string file in SafeFiles.Files(path)) files.Add(Path.GetRelativePath(directory, file), SafeFiles.Hash(file));
            else if (File.Exists(path)) files.Add(relative, SafeFiles.Hash(path));
        }
        if (!Directory.Exists(Path.Combine(directory, ".git")) ||
            SafeFiles.HashText(JsonSerializer.Serialize(files)) != source.BuildInputsHash ||
            (source == CSteamworks && (Directory.Exists(Path.Combine(directory, "wrapper")) || File.Exists(Path.Combine(directory, "wrapper")))))
            throw new InvalidDataException("Cached source differs from the pinned " + source.Name + " inputs: " + directory);
    }

    private static async Task<string> FingerprintAsync(string repository, string unityVersion, Inputs inputs, CancellationToken cancellation)
    {
        var values = new List<string> { "macos-platform-integration-v1", unityVersion };
        foreach (string variable in new[] { "CPATH", "CPLUS_INCLUDE_PATH", "DEVELOPER_DIR", "LIBRARY_PATH", "SDKROOT" })
            values.Add(Environment.GetEnvironmentVariable(variable) ?? "");
        values.Add(await ClangVersionAsync(cancellation));
        Tree(Path.Combine(inputs.Wrapper, "src"));
        if (File.Exists(Path.Combine(inputs.Wrapper, "wrapper/unitybuild.cpp"))) Tree(Path.Combine(inputs.Wrapper, "wrapper"));
        else
            foreach (string relative in CSteamworks.BuildInputs.Skip(1)) FileInput(Path.Combine(inputs.Wrapper, relative));
        Tree(inputs.SteamHeaders);
        FileInput(inputs.SteamApi);
        foreach (string relative in new[] { "api/core/inc", "api/studio/inc" })
            Tree(Path.Combine(inputs.FmodSdk, relative));
        foreach (string relative in new[] { "api/core/lib/libfmod.dylib", "api/studio/lib/libfmodstudio.dylib" })
            FileInput(Path.Combine(inputs.FmodSdk, relative));
        Tree(Path.Combine(inputs.OpenXrHeaders, "openxr"));
        Tree(inputs.UnityHeaders);
        Tree(Path.Combine(repository, "runtime", "UnityMetal"));
        Tree(Path.Combine(repository, "runtime", "Platform"));
        foreach (string project in new[] { NativeBuilderProject, UnityMetalProject })
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.Combine(repository, project))!)
                .Where(path => Path.GetExtension(path) is ".cs" or ".csproj").Order(StringComparer.Ordinal)) FileInput(file);
        foreach (string relative in new[] { "global.json", "Directory.Build.props", "src/H3VR.Converter/SafeFiles.cs" })
            FileInput(Path.Combine(repository, relative));
        return SafeFiles.HashText(JsonSerializer.Serialize(values));

        void Tree(string directory)
        {
            foreach (string file in SafeFiles.Files(SafeFiles.Canonical(directory)).Order(StringComparer.Ordinal)) FileInput(file);
        }

        void FileInput(string file)
        {
            cancellation.ThrowIfCancellationRequested();
            values.Add(file);
            values.Add(SafeFiles.Hash(file));
        }
    }

    private static async Task<string> ClangVersionAsync(CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("/usr/bin/xcrun", "clang++ --version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(info) ?? throw new IOException("Could not start xcrun.");
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0) throw new IOException("xcrun could not resolve the active macOS compiler. Check your Xcode command-line tools.");
            return output.Trim();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static Dictionary<string, string> Inventory(string directory) => SafeFiles.Files(directory)
        .Where(path => path != Path.Combine(directory, ReceiptName))
        .ToDictionary(path => Path.GetRelativePath(directory, path), SafeFiles.Hash, StringComparer.Ordinal);

    private static string Verify(string directory, string fingerprint)
    {
        var files = Inventory(directory); // Rejects links before the receipt is read.
        var receipt = JsonSerializer.Deserialize<CacheReceipt>(File.ReadAllText(Path.Combine(directory, ReceiptName)), ConversionConfig.Json);
        if (receipt is not { SchemaVersion: 1 } || receipt.Fingerprint != fingerprint || receipt.Files is null ||
            !files.ContainsKey(ManifestName) || receipt.Files.Count != files.Count ||
            files.Any(file => !receipt.Files.TryGetValue(file.Key, out string? hash) || file.Value != hash))
            throw new InvalidDataException("Platform integration cache integrity failed. Inspect the generated directory before rerunning: " + directory);
        return Path.Combine(directory, ManifestName);
    }
}
