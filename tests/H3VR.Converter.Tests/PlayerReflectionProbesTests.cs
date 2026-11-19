using System.Security.Cryptography;
using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class PlayerReflectionProbesTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Baked reflection probes preserve all six BC6H faces and their complete mip chains", Faces);
        yield return ("Player stamp binds the baked reflection repair without changing existing identities", Stamp);
    }

    private static void Faces()
    {
        foreach (var (width, mips, expected) in new[]
        {
            (64, 7, 5488), (128, 8, 21872), (256, 9, 87408), (512, 10, 349552), (1024, 11, 1398128)
        })
            if (PlayerReflectionProbes.CompleteFaceBytes(width, mips) != expected)
                throw new Exception("Incorrect linear BC6H mip extent.");

        const int smallFace = 64;
        byte[] bytes = Enumerable.Range(0, 6).SelectMany(face => Enumerable.Repeat((byte)face, smallFace)).ToArray();
        var hashes = PlayerReflectionProbes.FaceDigests(bytes, smallFace);
        var expectedHashes = Enumerable.Range(0, 6)
            .Select(face => Convert.ToHexStringLower(SHA256.HashData(Enumerable.Repeat((byte)face, smallFace).ToArray())));
        if (!hashes.SequenceEqual(expectedHashes)) throw new Exception("Cubemap face order changed.");
        try { PlayerReflectionProbes.FaceDigests(bytes[..^1], smallFace); throw new Exception("Truncated face accepted."); }
        catch (InvalidDataException) { }
        try { PlayerReflectionProbes.CompleteFaceBytes(1024, 10); throw new Exception("Incomplete mip chain accepted."); }
        catch (InvalidDataException) { }
    }

    private static void Stamp()
    {
        string folder = Directory.CreateTempSubdirectory("reflection-stamp-").FullName;
        try
        {
            string stamp = Path.Combine(folder, "nativeh3vr-build.json"), repair = Path.Combine(folder, "reflection-probes.json");
            File.WriteAllText(stamp, "{\"schemaVersion\":1,\"extractionFingerprint\":\"existing\",\"managedAssemblies\":{\"Game.dll\":\"original\"}}");
            File.WriteAllText(repair, "{\"schemaVersion\":1}");
            PlayerReceipt.BindReflectionProbes(stamp, repair);
            using var actual = System.Text.Json.JsonDocument.Parse(File.ReadAllText(stamp));
            var root = actual.RootElement;
            if (root.EnumerateObject().Count() != 4 || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("reflectionProbesSha256").GetString() != SafeFiles.Hash(repair) ||
                root.GetProperty("extractionFingerprint").GetString() != "existing" ||
                root.GetProperty("managedAssemblies").GetProperty("Game.dll").GetString() != "original")
                throw new Exception("Baked reflection receipt changed the player identity.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
