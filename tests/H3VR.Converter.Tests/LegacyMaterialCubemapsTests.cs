using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyMaterialCubemapsTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("material cubemaps accept exact authored complete and partial BC chains", FaceExtents),
        ("material cubemap native serialization retains six face-major streams and sampling", NativeLayout),
    ];

    private static void FaceExtents()
    {
        var cases = new[] { (2048, 12, 24, 5_592_432), (4096, 13, 24, 22_369_648),
            (2048, 1, 24, 4_194_304), (1024, 1, 10, 524_288), (1024, 11, 25, 1_398_128) };
        foreach (var (width, mips, format, expected) in cases)
        {
            if (LegacyMaterialCubemaps.MipFaceBytes(width, mips, format) != expected)
                throw new InvalidDataException("Incorrect original cubemap block extent.");
        }
        try { LegacyMaterialCubemaps.MipFaceBytes(128, 9, 24); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Accepted more mip levels than the original cube can hold.");
    }

    private static void NativeLayout()
    {
        var bytes = Enumerable.Range(0, 6).SelectMany(i => Enumerable.Repeat((byte)i, 112)).ToArray();
        var cube = new LegacyMaterialCubemaps.CubeData(8, 4, 24, 0, 2, 1, -0.25f, 1, 112, bytes);
        string directory = Path.Combine(Path.GetTempPath(), "NativeH3VR-Cube-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.cubemap");
            LegacyMaterialCubemaps.WriteCube(path, "Authored Test Cube", "0123456789abcdef0123456789abcdef", cube);
            string yaml = File.ReadAllText(path);
            foreach (string field in new[] { "--- !u!89 &8900000", "m_CompleteImageSize: 112",
                "m_TextureFormat: 24", "m_ImageCount: 6", "m_TextureDimension: 4", "m_MipBias: -0.25",
                "m_WrapW: 1", "image data: 672", "_typelessdata: " + Convert.ToHexStringLower(bytes) })
            {
                if (!yaml.Contains(field, StringComparison.Ordinal)) throw new InvalidDataException($"Missing {field}.");
            }
            if (yaml.Split("  - {fileID: 0}", StringSplitOptions.None).Length != 7 ||
                !File.ReadAllText(path + ".meta").Contains("mainObjectFileID: 8900000", StringComparison.Ordinal))
                throw new InvalidDataException("Native cubemap lacks an exact six-face type binding.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
