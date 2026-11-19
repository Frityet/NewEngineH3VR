using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using H3VR.Converter;

namespace UnityMetalCli;

internal static class MetalSmoke
{
    internal static async Task RunAsync(
        string unityEditor, string headers, string runtime, string work, int frames, CancellationToken cancellation)
    {
        const int EyeSize = 800;
        unityEditor = SafeFiles.Canonical(unityEditor);
        headers = SafeFiles.Canonical(headers);
        work = SafeFiles.Canonical(work);
        if (Directory.Exists(work) || File.Exists(work) || new FileInfo(work).LinkTarget is not null)
            throw new IOException("Choose a new Metal smoke work directory: " + work);
        string root = IntegrationBuild.SourceRoot();
        string source = Path.Combine(root, "runtime", "UnityMetal");
        string fixture = Path.Combine(root, "tests", "UnityMetal");
        string project = Path.Combine(work, "project");
        string assets = Path.Combine(project, "Assets");
        string editor = Directory.CreateDirectory(Path.Combine(assets, "Editor")).FullName;
        string plugins = Directory.CreateDirectory(Path.Combine(assets, "Plugins", "macOS")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(project, "ProjectSettings")).FullName;
        string packages = Directory.CreateDirectory(Path.Combine(project, "Packages")).FullName;
        File.WriteAllText(Path.Combine(settings, "ProjectVersion.txt"), $"m_EditorVersion: {ConversionConfig.TargetVersion}\n");
        File.WriteAllText(Path.Combine(assets, "csc.rsp"), "-define:NATIVEH3VR_METAL\n");
        File.WriteAllText(Path.Combine(packages, "manifest.json"),
            "{\"dependencies\":{\"com.unity.modules.xr\":\"1.0.0\",\"com.unity.modules.vr\":\"1.0.0\"}}\n");
        foreach (string file in IntegrationBuild.BridgeSources.Concat(["H3VRMetalBinding.cs", "SyntheticHands.cs", "StockGearNormals.cs"]))
            File.Copy(Path.Combine(source, file), Path.Combine(assets, file));
        File.Copy(Path.Combine(root, "runtime", "OpenXR", "OpenXRDeviceState.cs"), Path.Combine(assets, "OpenXRDeviceState.cs"));
        File.Copy(Path.Combine(fixture, "IndexRawPose.cs"), Path.Combine(assets, "IndexRawPose.cs"));
        File.Copy(Path.Combine(fixture, "MetalSmoke.cs"), Path.Combine(assets, "MetalSmoke.cs"));
        File.Copy(Path.Combine(fixture, "BuildMetalSmoke.cs"), Path.Combine(editor, "BuildMetalSmoke.cs"));
        await IntegrationBuild.BuildPluginAsync(unityEditor, headers, Path.Combine(plugins, IntegrationBuild.Library), cancellation);
        File.WriteAllText(Path.Combine(plugins, IntegrationBuild.Library + ".meta"), IntegrationBuild.PluginMetadata);

        string app = Path.Combine(work, "MetalSmoke.app");
        string editorLog = Path.Combine(work, "editor.log");
        var build = await ChildProcess.RunAsync(unityEditor,
            ["-batchmode", "-quit", "-projectPath", project, "-buildTarget", "OSXUniversal",
             "-executeMethod", "BuildMetalSmoke.Build", "-smokeOutput", app, "-logFile", editorLog],
             cancellation, timeout: TimeSpan.FromSeconds(180));
        if (build.ExitCode != 0)
            throw new IOException($"Unity smoke build exited {build.ExitCode}. See {editorLog}.");

        string state = Path.Combine(work, "state");
        string log = Path.Combine(work, "player.log");
        string capture = Path.Combine(work, "stereo.png");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["VR_OPENXR_TARGET_HZ"] = "90",
            ["NATIVEH3VR_EYE_WIDTH"] = EyeSize.ToString(CultureInfo.InvariantCulture),
            ["NATIVEH3VR_EYE_HEIGHT"] = EyeSize.ToString(CultureInfo.InvariantCulture),
            ["NATIVEH3VR_SMOKE_FRAMES"] = frames.ToString(CultureInfo.InvariantCulture)
        };
        int result = await PlayerRun.RunAsync(new Launch(app, runtime, state, log, true,
            ["-screen-fullscreen", "0", "-screen-width", "640", "-screen-height", "400"],
            Path.Combine(work, "macvr.csv"), Path.Combine(work, "unity.csv"), capture, TimeSpan.FromSeconds(100)),
            cancellation, environment);
        if (result != 0)
            throw new IOException($"Smoke player exited {result}. See {log}.");

        const string SavePrefix = "UNITY_METAL_SAVE_ROOT ";
        string[] lines = File.ReadAllLines(log);
        string? receipt = lines.FirstOrDefault(line => line.StartsWith("UNITY_METAL_SMOKE passed=True", StringComparison.Ordinal));
        string? savePath = lines.FirstOrDefault(line => line.StartsWith(SavePrefix, StringComparison.Ordinal));
        if (receipt is null || savePath is null || !SafeFiles.Contains(Path.Combine(state, "preferences"), savePath[SavePrefix.Length..]))
            throw new InvalidDataException("Missing player validation or isolated Unity save path. See " + log);

        double[][][] centers = await CheckCaptureAsync(capture, EyeSize, EyeSize, cancellation);
        Console.WriteLine(receipt);
        Console.WriteLine("COMPOSITOR_STEREO_MARKERS " + JsonSerializer.Serialize(centers));
        Console.WriteLine(capture);
    }

    private static async Task<double[][][]> CheckCaptureAsync(string capture, int width, int height,
                                                             CancellationToken cancellation)
    {
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("nh3vr-bitmap-");
        byte[] data;
        try
        {
            string bitmap = Path.Combine(temporary.FullName, "stereo.bmp");
            _ = await ChildProcess.CheckedAsync("/usr/bin/sips", ["-s", "format", "bmp", capture, "--out", bitmap],
                cancellation, captureOutput: true);
            data = File.ReadAllBytes(bitmap);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        return ReadStereoBitmap(data, width, height);
    }

    private static double[][][] ReadStereoBitmap(ReadOnlySpan<byte> data, int width, int height)
    {
        if (data.Length < 66 || data[0] != 'B' || data[1] != 'M')
            throw new InvalidDataException("Cannot decode the stereo bitmap.");

        int imageWidth = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int storedHeight = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        if (imageWidth != width * 2 || (storedHeight != height && storedHeight != -height) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) != 32 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[30..]) != 3)
            throw new InvalidDataException("Unexpected macOS stereo bitmap format.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[54..]) != 0xff0000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[58..]) != 0x00ff00 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[62..]) != 0x0000ff)
            throw new InvalidDataException("Unexpected macOS stereo bitmap channel masks.");
        int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[10..]));
        int rowBytes = imageWidth * 4;
        if (offset < 66 || (long)offset + (long)height * rowBytes > data.Length)
            throw new InvalidDataException("Truncated macOS stereo bitmap.");

        var markers = new Marker[2, 2];
        for (int row = 0; row < height; row++)
        {
            int y = storedHeight < 0 ? row : height - row - 1;
            ReadOnlySpan<byte> pixels = data.Slice(offset + row * rowBytes, rowBytes);
            for (int x = 0; x < imageWidth; x++)
            {
                int pixel = x * 4;
                byte blue = pixels[pixel], green = pixels[pixel + 1], red = pixels[pixel + 2];
                if (red >= 160 && green < 90 && blue < 90)
                    markers[x / width, 0].Add(x % width, y);
                if (green >= 160 && red < 90 && blue < 90)
                    markers[x / width, 1].Add(x % width, y);
            }
        }

        double[][][] centers = [[markers[0, 0].Center(), markers[0, 1].Center()],
                                [markers[1, 0].Center(), markers[1, 1].Center()]];
        if (centers.Any(eye => eye[0][1] - eye[1][1] < 20))
            throw new InvalidDataException("Compositor eye textures are vertically inverted.");
        if (centers[0][0][0] - centers[1][0][0] < 2)
            throw new InvalidDataException("Compositor eye textures lack binocular parallax.");
        return centers;
    }

    private struct Marker
    {
        private long count;
        private long sumX;
        private long sumY;

        internal void Add(int x, int y)
        {
            count++;
            sumX += x;
            sumY += y;
        }

        internal readonly double[] Center()
        {
            if (count < 32)
                throw new InvalidDataException("Both compositor eyes must contain the red and green markers.");
            return [(double)sumX / count, (double)sumY / count];
        }
    }
}
