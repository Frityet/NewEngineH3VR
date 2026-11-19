using System.Buffers.Binary;
using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class PlayerEnlightenTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Enlighten validates scene identities and probe mapping layout", Mapping);
        yield return ("Enlighten rejects an unpinned database before creating staging", RejectUnpinned);
    }
    private static void Mapping()
    {
        string[] hashes = ["00112233445566778899aabbccddeeff", "ffeeddccbbaa99887766554433221100"];
        byte[] bytes = PlayerEnlighten.CreateProbeMapping(hashes);
        if (bytes.Length != 52 || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)) != 2 ||
            !bytes.AsSpan(12, 16).SequenceEqual(Convert.FromHexString(hashes[0])) ||
            !bytes.AsSpan(28, 16).SequenceEqual(Convert.FromHexString(hashes[1])) ||
            bytes.AsSpan(44, 8).ToArray().Any(b => b != 0)) throw new Exception("Incorrect serialized mapping");
        foreach (var invalid in new[] { Array.Empty<string>(), new[] { hashes[0], hashes[0] }, new[] { "invalid" }, new[] { hashes[0].ToUpperInvariant() } })
        {
            try { PlayerEnlighten.CreateProbeMapping(invalid); throw new Exception("Invalid hashes accepted"); }
            catch (InvalidDataException) { }
        }
        if (PlayerEnlighten.CanonicalScene(@"Assets\Scenes\Test.unity") != "Assets/Scenes/Test.unity") throw new Exception("Canonical path mismatch");
        foreach (string invalid in new[] { "Assets/../Escape.unity", "/Assets/Scene.unity", "Assets//Scene.unity", "Assets/Scene.txt" })
        {
            try { PlayerEnlighten.CanonicalScene(invalid); throw new Exception("Unsafe path accepted"); }
            catch (InvalidDataException) { }
        }
    }
    private static void RejectUnpinned()
    {
        string root = Path.Combine(Path.GetTempPath(), "enlighten-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), output = Path.Combine(root, "output"), db = Path.Combine(root, "database");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target); File.WriteAllText(db, "synthetic");
            try { PlayerEnlighten.Stage(source, target, db, new string('0', 64), output); throw new Exception("Unpinned database accepted"); }
            catch (InvalidDataException) { }
            if (Directory.Exists(output)) throw new Exception("Staging created after pin failure");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
