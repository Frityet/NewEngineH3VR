using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace H3VR.Converter;

internal static class LocalCMakeProvisioning
{
    private const string Release = "3.31.12";
    private static readonly Version Minimum = new(3, 22, 1);
    private static readonly Version Pinned = Version.Parse(Release);
    private const string ReleaseUrl = "https://github.com/Kitware/CMake/releases/download/v" + Release + "/";

    private sealed record Package(string Platform, string ArchiveHash, string ContentsHash)
    {
        internal bool Zip => Platform.StartsWith("windows-", StringComparison.Ordinal);
        internal string Name => "cmake-" + Release + "-" + Platform;
        internal string Archive => Name + (Zip ? ".zip" : ".tar.gz");
        internal string Prefix => Name + (Platform == "macos-universal" ? "/CMake.app/Contents/" : "/");
        internal string Executable(string directory) => Path.Combine(directory, "bin", Zip ? "cmake.exe" : "cmake");
    }

    private static Package HostPackage() => (RuntimeInformation.ProcessArchitecture, OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), OperatingSystem.IsWindows()) switch
    {
        (Architecture.Arm64 or Architecture.X64, true, _, _) => new("macos-universal",
            "799af7fd545db9bf1b9cfe72f8095880e727a2d4e0df0e3dffc3bc7b95c2d3b0", "49b419435d83ec29119d947c346c1ea86d2f299c4a21ce27095f0f525bd17b76"),
        (Architecture.X64, _, true, _) => new("linux-x86_64",
            "0dc2e9a6860f06bf10bd8fadc03e35d9eeb4df46e33763a7e480e987758f385c", "a861702f387fa3cbca04ef4d6566166251a7bd85ce17845953d552afe0510d31"),
        (Architecture.Arm64, _, true, _) => new("linux-aarch64",
            "83f8fd91d2038a56556e1400390fcfe42f79602940c494f6c6f1cdae7f9e7f40", "1edaa35d3b4a6d621f39f01f4d7b281ca903430422e661c32c816ee40b5a01e9"),
        (Architecture.X64, _, _, true) => new("windows-x86_64",
            "0c4baa40f28b3f8225eb3fdf6946c987b4fe901403b4eaf2fbbd9378100aaa0c", "984eefce9cb2bd87166855f21a96ba14d1a6204d847e6f458933987f789fb938"),
        (Architecture.Arm64, _, _, true) => new("windows-arm64",
            "e4160c1842dea858ad376ff2ec17587104515b51714eca5963b8bdd798105553", "c21aa8e0a53c3d920f93e05a76f3bb42092db3349f7c718a3d97906ddd48ecae"),
        _ => throw new PlatformNotSupportedException($"Use CMake {Minimum} or newer on PATH for this platform.")
    };

    internal static async Task<string> ResolveAsync(string repository, ConversionConfig config, CancellationToken cancellation)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string executable = Path.GetFullPath(Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "cmake.exe" : "cmake"));
            if (File.Exists(executable) && await ReadVersionAsync(executable, cancellation) >= Minimum) return executable;
        }

        Package package = HostPackage();
        string parent = Path.Combine(config.CacheDirectory(repository), "cmake");
        string destination = Path.Combine(parent, package.Name);
        SafeFiles.RequireToolCachePath(repository, destination);
        if (Directory.Exists(destination) || File.Exists(destination)) return Verify(destination, package, cancellation);
        if (config.Offline)
            throw new InvalidOperationException($"Offline mode needs CMake {Minimum} or newer on PATH or the verified cached CMake at {destination}.");

        string staging = Directory.CreateDirectory(Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string archive = Path.Combine(staging, package.Archive);
            Console.WriteLine($"Downloading CMake {Release} for {package.Platform} into {parent}");
            await DownloadAsync(package, archive, cancellation);
            if (SafeFiles.Hash(archive) != package.ArchiveHash)
                throw new InvalidDataException("The downloaded CMake archive failed its pinned SHA-256 check.");

            string payload = Directory.CreateDirectory(Path.Combine(staging, "payload")).FullName;
            await ExtractAsync(archive, payload, package, cancellation);
            string executable = Verify(payload, package, cancellation);
            if (await ReadVersionAsync(executable, cancellation) != Pinned)
                throw new InvalidDataException("The verified CMake executable could not report the pinned release " + Release + ".");
            cancellation.ThrowIfCancellationRequested();
            SafeFiles.RequireToolCachePath(repository, destination);
            try { Directory.Move(payload, destination); }
            catch (IOException) when (Directory.Exists(destination)) { return Verify(destination, package, cancellation); }
            return package.Executable(destination);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    private static async Task<Version?> ReadVersionAsync(string executable, CancellationToken cancellation)
    {
        const string prefix = "cmake version ";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return null;
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                await Task.WhenAll(output, process.StandardError.ReadToEndAsync(deadline.Token), process.WaitForExitAsync(deadline.Token));
                string line = (await output).Split('\n', 2)[0].TrimEnd();
                return process.ExitCode == 0 && line.StartsWith(prefix, StringComparison.Ordinal) &&
                    Version.TryParse(line[prefix.Length..].Split('-', 2)[0], out var version) ? version : null;
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Win32Exception) { return null; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
    }

    private static async Task DownloadAsync(Package package, string destination, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            await using var download = await http.GetStreamAsync(ReleaseUrl + package.Archive, deadline.Token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
            await download.CopyToAsync(output, deadline.Token);
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException("The pinned Kitware CMake archive download timed out.", error);
        }
    }

    private static async Task ExtractAsync(string archive, string payload, Package package, CancellationToken cancellation)
    {
        string unpacked = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(archive)!, "unpacked")).FullName;
        if (package.Zip)
            ZipFile.ExtractToDirectory(archive, unpacked);
        else
            await Processes.Run(OperatingSystem.IsMacOS() ? "/usr/bin/tar" : "tar", ["-xzf", archive, "-C", unpacked],
                unpacked, cancellation: cancellation);

        // Publish only the CLI and its associated modules, documentation and licenses.
        string contents = SafeFiles.Inside(unpacked, package.Prefix);
        foreach (string directory in new[] { "bin", "doc", "man", "share" })
        {
            string source = SafeFiles.Inside(contents, directory);
            if (Directory.Exists(source)) Directory.Move(source, Path.Combine(payload, directory));
        }
        File.Delete(Path.Combine(payload, "bin", package.Zip ? "cmake-gui.exe" : "cmake-gui"));
    }

    private static string Verify(string directory, Package package, CancellationToken cancellation)
    {
        var files = Directory.Exists(directory) ? SafeFiles.Files(directory).Select(path => (
            Path: path, Relative: Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal) : null;
        // This digest covers paths and bytes of the CLI distribution extracted from the checked archive.
        var inventory = files?.Select(file =>
        {
            cancellation.ThrowIfCancellationRequested();
            return file.Relative + '\0' + SafeFiles.Hash(file.Path) + '\n';
        });
        if (inventory is null || SafeFiles.HashText(string.Concat(inventory)) != package.ContentsHash)
            throw new InvalidDataException("The cached CMake distribution failed integrity verification. Choose a different --cache or move it aside: " + directory);
        string executable = package.Executable(directory);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
            throw new InvalidDataException("The verified CMake binary lacks execute permission: " + executable);
        return executable;
    }
}
