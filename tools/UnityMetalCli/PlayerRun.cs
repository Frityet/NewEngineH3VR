using System.Runtime.InteropServices;
using System.Text.Json;
using H3VR.Converter;
using Microsoft.Win32.SafeHandles;

namespace UnityMetalCli;

internal sealed record Launch(
    string App, string Runtime, string State, string Log, bool Synthetic, string[] PlayerArguments,
    string? Trace = null, string? Profile = null, string? Capture = null, TimeSpan? Timeout = null, string? Mods = null,
    string? Renderer = null, string? RunCheckout = null);

internal static class PlayerRun
{
    private static readonly string[] RuntimeFiles =
        ["macvr.json", "libMacVROpenXR.dylib", "libIndexCore.dylib", "libopenxr_loader.dylib"];

    private sealed record MacVR(string Manifest, string Loader, string Version, string Revision);
    internal sealed record Player(string Program, bool Stock, bool SupportsExternalMods);

    internal static async Task<int> RunAsync(
        Launch launch, CancellationToken cancellation, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Launching an Apple Silicon player requires macOS.");

        string repository = launch.RunCheckout is null
            ? IntegrationBuild.SourceRoot()
            : SafeFiles.Canonical(launch.RunCheckout);
        if (!Directory.Exists(repository))
            throw new DirectoryNotFoundException("The run checkout does not exist: " + repository);
        foreach (string output in new[] { launch.State, launch.Log, launch.Trace, launch.Profile, launch.Capture }.OfType<string>())
            SafeFiles.RequirePrivateOutput(repository, output);
        string app = SafeFiles.Canonical(launch.App);
        string runtimeRoot = SafeFiles.Canonical(launch.Runtime);
        string state = SafeFiles.Canonical(launch.State);
        var runtime = VerifyRuntime(runtimeRoot);
        string profileApp = RendererPackage.Select(app, launch.Renderer);
        if (profileApp != app)
            await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--verify", "--strict", "--ignore-resources", app], cancellation);
        Player player = await VerifyPlayerAsync(profileApp, cancellation);
        if (!launch.Synthetic && !player.Stock)
            throw new InvalidDataException("A headset launch requires a locally imported NativeH3VR app.");
        string? mods = ResolveOptional(launch.Mods);
        if (mods is not null)
        {
            if (!player.SupportsExternalMods)
                throw new InvalidDataException("This player build does not support external mod directories.");
            if (!Directory.Exists(mods))
                throw new DirectoryNotFoundException("The external mod directory does not exist: " + mods);
            SafeFiles.RequireSeparate(app, mods);
        }

        bool InsideInput(string path) => SafeFiles.Contains(app, path) || SafeFiles.Contains(runtimeRoot, path);
        if (InsideInput(state))
            throw new InvalidDataException("Save state must be outside the signed app and runtime package.");

        string log = SafeFiles.Canonical(launch.Log);
        string? trace = ResolveOptional(launch.Trace);
        string? profile = ResolveOptional(launch.Profile);
        string? capture = ResolveOptional(launch.Capture);
        string[] outputs = new[] { log, trace, profile, capture }.OfType<string>().ToArray();
        if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length)
            throw new InvalidDataException("Each run output must have a distinct path.");
        if (outputs.Any(path => Exists(path) || InsideInput(path)))
            throw new InvalidDataException("Choose fresh output paths outside the signed app and runtime package.");
        if (launch.PlayerArguments.Any(arg => arg.StartsWith("-logfile", StringComparison.OrdinalIgnoreCase) ||
              arg.Equals("-nographics", StringComparison.OrdinalIgnoreCase) ||
              arg.Equals("-force-glcore", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The launcher manages the player log and requires Metal.");

        Directory.CreateDirectory(state);
        using var lockedState = AcquireLock(state);
        string settings = StateSubdirectory(state, "preferences");
        var environment = extraEnvironment is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(extraEnvironment, StringComparer.Ordinal);
        environment["XR_API_LAYER_PATH"] = null;
        environment["XR_ENABLE_API_LAYERS"] = null;
        environment["VR_OPENXR_CAPTURE_EVERY"] = null;
        environment["XR_RUNTIME_JSON"] = runtime.Manifest;
        environment["NATIVEH3VR_OPENXR_LOADER"] = runtime.Loader;
        environment["NATIVEH3VR_STATE_DIR"] = state;
        environment["NATIVEH3VR_MODS_DIR"] = mods;
        environment["CFFIXED_USER_HOME"] = settings;
        environment["VR_OPENXR_SIMULATE"] = launch.Synthetic ? "1" : "0";
        environment["VR_OPENXR_HEADLESS"] = launch.Synthetic ? "1" : "0";
        environment["VR_OPENXR_FRAME_TRACE"] = trace;
        environment["NATIVEH3VR_UNITY_TRACE"] = profile;
        environment["VR_OPENXR_CAPTURE"] = capture;
        if (launch.Synthetic)
        {
            environment["VR_STATE_DIR"] = StateSubdirectory(state, "macvr");
            // Synthetic tracking uses no broker. A relative endpoint stays under the player's working directory.
            environment["VR_TRACKING_SOCKET"] = "tracking-" + Path.GetRandomFileName() + ".sock";
        }
        else if (!environment.ContainsKey("VR_STATE_DIR") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VR_STATE_DIR")))
        {
            // MacVR normally resolves calibration from the user's Application Support directory.
            // Resolve it before the isolated Unity preferences redirect the child's home.
            string support = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string current = Path.Combine(support, "MacVR");
            environment["VR_STATE_DIR"] = new[] { current, Path.Combine(support, "Virtual Reality"), Path.Combine(support, "IndexMac") }
                .FirstOrDefault(Directory.Exists) ?? current;
        }
        if (player.Stock)
        {
            environment["SteamAppId"] = "450540";
            environment["SteamGameId"] = "450540";
        }

        foreach (string output in outputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (Exists(output))
                throw new IOException("Run output already exists: " + output);
        }
        Console.WriteLine($"MacVR {runtime.Version} revision {runtime.Revision} from {runtime.Manifest}");
        if (profileApp != app) Console.WriteLine($"NativeH3VR renderer: {launch.Renderer ?? "base"}");
        Console.Out.Flush();
        var result = await ChildProcess.RunAsync(player.Program,
            ["-force-metal", "-logFile", log, .. launch.PlayerArguments],
            cancellation, workingDirectory: state, environment: environment, timeout: launch.Timeout);
        return result.ExitCode;
    }

    private static string? ResolveOptional(string? path) => path is null ? null : SafeFiles.Canonical(path);

    private static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static MacVR VerifyRuntime(string root)
    {
        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "receipt.json")));
        JsonElement metadata = receipt.RootElement;
        if (metadata.GetProperty("schemaVersion").GetInt32() != 1 ||
            metadata.GetProperty("architecture").GetString() != "arm64" ||
            metadata.GetProperty("name").GetString() != "MacVR")
            throw new InvalidDataException("Expected a schema 1 arm64 MacVR OpenXR package.");
        JsonElement hashes = metadata.GetProperty("files");
        foreach (string name in RuntimeFiles)
        {
            string file = Path.Combine(root, name);
            if (!File.Exists(file) || new FileInfo(file).LinkTarget is not null ||
                !hashes.TryGetProperty(name, out var sha) || sha.GetString() != SafeFiles.Hash(file))
                throw new InvalidDataException("MacVR package hash mismatch for " + name);
        }
        string manifest = Path.Combine(root, RuntimeFiles[0]);
        using JsonDocument runtimeManifest = JsonDocument.Parse(File.ReadAllText(manifest));
        string? library = runtimeManifest.RootElement.GetProperty("runtime").GetProperty("library_path").GetString();
        if (library is not ("libMacVROpenXR.dylib" or "./libMacVROpenXR.dylib"))
            throw new InvalidDataException("Expected a relocatable MacVR runtime manifest.");
        return new(manifest, Path.Combine(root, RuntimeFiles[^1]),
            metadata.GetProperty("version").GetString()!, metadata.GetProperty("sourceRevision").GetString()!);
    }

    internal static async Task<Player> VerifyPlayerAsync(string app, CancellationToken cancellation)
    {
        string contents = Path.Combine(app, "Contents");
        string plist = Path.Combine(contents, "Info.plist");
        string name = (await ChildProcess.CheckedAsync("/usr/bin/plutil",
            ["-extract", "CFBundleExecutable", "raw", "-o", "-", plist], cancellation, captureOutput: true)).TrimEnd('\r', '\n');
        if (!IsLeaf(name))
            throw new InvalidDataException("Invalid app executable name.");
        string program = Path.Combine(contents, "MacOS", name);
        await IntegrationBuild.RequireArm64Async(program, cancellation);
        await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], cancellation);

        string stamp = Path.Combine(contents, "Resources", "nativeh3vr-build.json");
        if (!File.Exists(stamp))
            return new(program, false, false);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(stamp));
        JsonElement receipt = document.RootElement;
        if (receipt.GetProperty("schemaVersion").GetInt32() != 1 ||
            receipt.GetProperty("architecture").GetString() != "arm64" ||
            receipt.GetProperty("buildTarget").GetString() != "StandaloneOSX" ||
            receipt.GetProperty("playerExecutable").GetString() != name)
            throw new InvalidDataException("Unsupported NativeH3VR app receipt.");
        string managed = Path.Combine(contents, "Resources", "Data", "Managed");
        foreach (JsonProperty entry in receipt.GetProperty("managedAssemblies").EnumerateObject())
        {
            if (!IsLeaf(entry.Name) || SafeFiles.Hash(Path.Combine(managed, entry.Name)) != entry.Value.GetString())
                throw new InvalidDataException("Stock managed file failed receipt verification: " + entry.Name);
        }
        bool externalMods = receipt.TryGetProperty("modHostSchemaVersion", out var modHost)
            && modHost.ValueKind == JsonValueKind.Number && modHost.TryGetInt32(out var version) && version == 2;
        return new(program, true, externalMods);
    }

    private static bool IsLeaf(string name) =>
        name.Length != 0 && name is not ("." or "..") && !name.Any(char.IsControl) && Path.GetFileName(name) == name;

    private static string StateSubdirectory(string state, string name)
    {
        string path = Path.Combine(state, name);
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Private state paths must not be symlinks: " + path);
        return Directory.CreateDirectory(path).FullName;
    }

    private static FileStream AcquireLock(string state)
    {
        string path = Path.Combine(state, ".nativeh3vr.lock");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("The player lock must not be a symlink: " + path);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        const int LockExclusive = 2;
        const int LockNonblocking = 4;
        if (Flock(stream.SafeFileHandle, LockExclusive | LockNonblocking) == 0)
            return stream;
        int error = Marshal.GetLastPInvokeError();
        stream.Dispose();
        throw new IOException($"Could not lock private state directory {state} (errno {error}).");
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle file, int operation);
}
