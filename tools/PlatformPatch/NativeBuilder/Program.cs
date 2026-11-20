using System.Diagnostics;
using System.Text.RegularExpressions;
using H3VR.Converter;

return await NativeBuilder.Run(args);

internal static partial class NativeBuilder
{
    private const string CSteamworksRevision = "407dcba0151cfa52d1814a752e3038f16dbedc4d";
    private const string Usage = """
        Build private macOS native platform inputs from local SDKs.
        dotnet run --project tools/PlatformPatch/NativeBuilder -c Release -- \
          --csteamworks PATH --steam-api PATH --fmod-sdk PATH --output PATH

          --csteamworks        Clean checkout of the pinned CSteamworks wrapper.
          --steamworks-headers Steamworks SDK 1.41 public/steam directory (defaults to <csteamworks>/steam).
          --steam-api          Installed Steam client libsteam_api.dylib.
          --fmod-sdk           FMOD 2.03.15 SDK directory containing api/.
          --output             New directory under the checkout's .local/ directory, separate from the inputs.
        """;

    private static readonly string[] RequiredOptions = ["--csteamworks", "--steam-api", "--fmod-sdk", "--output"];

    public static async Task<int> Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try
        {
            var options = Parse(args);
            if (!OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("The native platform builder requires macOS.");
            await Build(options, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Native platform build cancelled.");
            return 130;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Native platform build failed. {error.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= Cancel; }

        void Cancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            var argument = args[index].Split('=', 2);
            var key = argument[0];
            if (!(RequiredOptions.Contains(key) || key == "--steamworks-headers") || options.ContainsKey(key))
                throw new ArgumentException($"Unknown or repeated flag {key}. See --help.");
            var value = argument.Length == 2 ? argument[1] : index + 1 < args.Length ? args[++index] : "";
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Set a path for {key}. See --help.");
            options.Add(key, key == "--output" ? Path.GetFullPath(value) : SafeFiles.Canonical(value));
        }
        var missing = RequiredOptions.Where(key => !options.ContainsKey(key)).ToArray();
        if (missing.Length != 0) throw new ArgumentException($"Required flags: {string.Join(", ", missing)}.");
        return options;
    }

    private static async Task Build(Dictionary<string, string> options, CancellationToken cancellation)
    {
        var repository = Repository.Find();
        var output = options["--output"];
        var wrapper = options["--csteamworks"];
        var steamHeaders = options.GetValueOrDefault("--steamworks-headers") ?? Path.Combine(wrapper, "steam");
        var steamApi = options["--steam-api"];
        var sdk = Path.Combine(options["--fmod-sdk"], "api");
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Output already exists. Choose a new directory.");
        SafeFiles.RequirePrivateOutput(repository, output);
        foreach (var input in new[] { wrapper, steamHeaders, options["--fmod-sdk"], Path.GetDirectoryName(steamApi)! })
            SafeFiles.RequireSeparate(input, output);
        if (!File.Exists(Path.Combine(steamHeaders, "steam_api.h")))
            throw new InvalidDataException("Supply --steamworks-headers with the Steamworks SDK 1.41 public/steam directory.");

        if (await Git(wrapper, ["rev-parse", "HEAD"], cancellation) != CSteamworksRevision)
            throw new InvalidDataException("CSteamworks revision differs from the verified SDK 1.41 wrapper.");
        if ((await Git(wrapper, ["status", "--porcelain", "--untracked-files=no"], cancellation)).Length != 0)
            throw new InvalidDataException("CSteamworks tracked sources have local changes.");
        var header = Path.Combine(sdk, "core/inc/fmod_common.h");
        if (!FmodVersion().IsMatch(File.ReadAllText(header)))
            throw new InvalidDataException("FMOD SDK headers must be version 2.03.15.");

        var libraries = new Dictionary<string, string>
        {
            ["libsteam_api.dylib"] = steamApi,
            ["libfmod.dylib"] = Path.Combine(sdk, "core/lib/libfmod.dylib"),
            ["libfmodstudio.dylib"] = Path.Combine(sdk, "studio/lib/libfmodstudio.dylib"),
        };
        foreach (var source in libraries.Values) await Execute("lipo", "-verify_arch", "arm64", source);

        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, "." + Path.GetFileName(output) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var (name, source) in libraries)
            {
                var destination = Path.Combine(stage, name);
                SafeFiles.Copy(source, destination);
                await Execute("install_name_tool", "-id", "@rpath/" + name, destination);
            }
            var bindings = Path.Combine(wrapper, "wrapper");
            var generation = Path.Combine(stage, "bindings");
            if (!File.Exists(Path.Combine(bindings, "unitybuild.cpp")))
                bindings = await GenerateBindings(wrapper, steamHeaders, generation, cancellation);
            await Compile(repository, wrapper, steamHeaders, bindings, sdk, stage, cancellation);
            if (Directory.Exists(generation)) Directory.Delete(generation, recursive: true);
            var entries = new List<PlatformFile>();
            foreach (var name in libraries.Keys.Append("libCSteamworks.dylib").Append("libNativeH3VRFMOD.dylib"))
            {
                var path = Path.Combine(stage, name);
                await Execute("lipo", "-verify_arch", "arm64", path);
                await Execute("codesign", "--force", "--sign", "-", path);
                await Execute("codesign", "--verify", "--strict", path);
                File.WriteAllText(path + ".meta", PluginMetadata(name));
                foreach (var file in new[] { path, path + ".meta" })
                {
                    var basename = Path.GetFileName(file);
                    entries.Add(new PlatformFile(basename, "Assets/Plugins/Native/macOS/" + basename, SafeFiles.Hash(file)));
                }
            }
            SafeFiles.AtomicJson(Path.Combine(stage, "platform.json"), new { Files = entries });
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, output);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
        Console.WriteLine("Private platform fragment: " + Path.Combine(output, "platform.json"));

        Task Execute(string program, params string[] arguments) =>
            Processes.Run(program, arguments, repository, cancellation: cancellation);
    }

    private static async Task<string> GenerateBindings(string source, string headers, string directory, CancellationToken cancellation)
    {
        string generator = Path.Combine(source, "CSteamworks2.py");
        if (!File.Exists(generator) || !File.Exists(Path.Combine(source, "SteamworksParser/steamworksparser.py")))
            throw new InvalidDataException("Initialize the pinned CSteamworks SteamworksParser submodule to generate the bindings.");
        SafeFiles.CopyTree(SafeFiles.Canonical(headers), Path.Combine(directory, "steam"));
        // Upstream enumerates headers with os.listdir. Sort that input so the same SDK produces identical bindings.
        const string run = """
            import os, runpy, sys
            listdir = os.listdir
            os.listdir = lambda path: sorted(listdir(path))
            sys.path.insert(0, os.path.dirname(sys.argv[1]))
            runpy.run_path(sys.argv[1], run_name='__main__')
            """;
        await Processes.Run("/usr/bin/python3", ["-I", "-B", "-c", run, generator], directory, cancellation: cancellation);
        var result = Path.Combine(directory, "wrapper");
        if (!File.Exists(Path.Combine(result, "unitybuild.cpp")))
            throw new InvalidDataException("CSteamworks did not generate its Steamworks SDK bindings.");
        return result;
    }

    private static async Task Compile(string repository, string wrapper, string steamHeaders, string bindings, string sdk, string stage,
        CancellationToken cancellation)
    {
        string[] common = ["-arch", "arm64", "-O2", "-dynamiclib", "-mmacosx-version-min=12.0"];
        var platform = Path.Combine(repository, "runtime/Platform");
        string[] steam =
        [
            .. common, "-std=c++11", "-DPOSIX", "-DOSX", "-DCSTEAMWORKS_DLLEXPORT", "-DUSE_BREAKPAD_HANDLER",
            "-I" + steamHeaders, "-I" + Path.Combine(wrapper, "src"), "-I" + bindings,
            Path.Combine(wrapper, "src/steam_api.cpp"), Path.Combine(platform, "Steam/legacy_api.cpp"),
            "-L" + stage, "-lsteam_api", "-Wl,-rpath,@loader_path",
            "-Wl,-install_name,@rpath/libCSteamworks.dylib", "-o", Path.Combine(stage, "libCSteamworks.dylib"),
        ];
        string[] fmod =
        [
            .. common, "-std=c++17", "-I" + Path.Combine(sdk, "core/inc"), "-I" + Path.Combine(sdk, "studio/inc"),
            Path.Combine(platform, "Fmod/bridge.cpp"), Path.Combine(platform, "Fmod/fmod-output.cpp"),
            "-L" + stage, "-lfmod", "-lfmodstudio",
            "-Xlinker", "-reexport_library", "-Xlinker", Path.Combine(stage, "libfmod.dylib"),
            "-Xlinker", "-reexport_library", "-Xlinker", Path.Combine(stage, "libfmodstudio.dylib"),
            "-Wl,-rpath,@loader_path", "-Wl,-install_name,@rpath/libNativeH3VRFMOD.dylib",
            "-o", Path.Combine(stage, "libNativeH3VRFMOD.dylib"),
        ];
        await Processes.Run("clang++", steam, repository, cancellation: cancellation);
        await Processes.Run("clang++", fmod, repository, cancellation: cancellation);
    }

    private static async Task<string> Git(string directory, string[] arguments, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var item in new[] { "-C", directory }.Concat(arguments)) info.ArgumentList.Add(item);
        using var process = Process.Start(info) ?? throw new IOException("Could not start Git.");
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0) throw new IOException("Could not verify CSteamworks checkout.");
            return output.Trim();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static string PluginMetadata(string name) => $$"""
        fileFormatVersion: 2
        guid: {{SafeFiles.HashText("NativeH3VR.Platform:" + name)[..32]}}
        PluginImporter:
          externalObjects: {}
          serializedVersion: 2
          isPreloaded: 0
          isOverridable: 0
          isExplicitlyReferenced: 0
          validateReferences: 1
          platformData:
          - first:
              Any:
            second:
              enabled: 0
              settings: {}
          - first:
              Editor: Editor
            second:
              enabled: 0
              settings:
                DefaultValueInitialized: true
          - first:
              Standalone: OSXUniversal
            second:
              enabled: 1
              settings:
                CPU: ARM64
                OS: OSX
          userData:
          assetBundleName:
          assetBundleVariant:
        """ + "\n";

    [GeneratedRegex(@"(?m)^[ \t]*#[ \t]*define[ \t]+FMOD_VERSION[ \t]+0x00020315\b")]
    private static partial Regex FmodVersion();

    private sealed record PlatformFile(string Source, string Destination, string Sha256);
}
