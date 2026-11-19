using System.Buffers.Binary;
using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class PlayerEnlightenTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Enlighten validates scene identities and probe mapping layout", Mapping);
        yield return ("Enlighten rejects an unpinned database before creating staging", RejectUnpinned);
        yield return ("Enlighten installs and safely cleans a verified staged delta", InstallAndCleanup);
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
    private static void InstallAndCleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), "enlighten-install-test-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target"), stage = Path.Combine(root, "stage"), evidence = Path.Combine(root, "evidence");
        const string probe = "00112233445566778899aabbccddeeff";
        string pos = "GI/level0/00/" + probe + ".pos", rpc = "GI/level0/00/" + probe + ".rpc.sse";
        static void Write(string root, string relative, string content) { string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        try
        {
            Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
            Write(target, "globalgamemanagers", "settings"); Write(target, "level0", "original-level");
            Write(stage, "level0", "staged-level"); Write(stage, pos, "position"); Write(stage, rpc, "probe-core");
            Write(target, pos, "position"); // Already matching files are never claimed or removed.
            var files = new Dictionary<string, string> { ["level0"] = SafeFiles.Hash(Path.Combine(stage, "level0")), [pos] = SafeFiles.Hash(Path.Combine(stage, pos)), [rpc] = SafeFiles.Hash(Path.Combine(stage, rpc)) };
            var scene = new PlayerEnlighten.SceneReceipt("Assets/Scenes/Test.unity", "level38", "level0", 1, new string('0', 64), SafeFiles.Hash(Path.Combine(target, "level0")), files["level0"], [probe]);
            var receipt = new PlayerEnlighten.Receipt(1, Path.Combine(root, "source"), target, new string('0', 64), new string('0', 64), SafeFiles.Hash(Path.Combine(target, "globalgamemanagers")), [scene], files);
            Write(stage, "enlighten-stage.json", System.Text.Json.JsonSerializer.Serialize(receipt));
            string receiptHash = SafeFiles.Hash(Path.Combine(stage, "enlighten-stage.json"));
            Write(stage, "unexpected", "x");
            try { PlayerEnlighten.Install(stage, target, receiptHash, evidence); throw new Exception("Unexpected staged file accepted"); } catch (InvalidDataException) { }
            File.Delete(Path.Combine(stage, "unexpected"));
            Write(target, rpc, "unrelated");
            try { PlayerEnlighten.Install(stage, target, receiptHash, evidence); throw new Exception("Unrelated file overwritten"); } catch (InvalidDataException) { }
            if (File.ReadAllText(Path.Combine(target, "level0")) != "original-level" || Directory.Exists(evidence)) throw new Exception("Failed preflight changed target");
            File.Delete(Path.Combine(target, rpc));
            var installed = PlayerEnlighten.Install(stage, target, receiptHash, evidence);
            if (File.ReadAllText(Path.Combine(target, "level0")) != "staged-level" || !File.Exists(Path.Combine(target, rpc))) throw new Exception("Installation missing files");
            Write(target, rpc, "external-change");
            try { PlayerEnlighten.Cleanup(target, evidence, installed.ReceiptSha256); throw new Exception("Changed file removed"); } catch (InvalidDataException) { }
            if (File.ReadAllText(Path.Combine(target, "level0")) != "staged-level") throw new Exception("Failed cleanup changed target");
            Write(target, rpc, "probe-core");
            PlayerEnlighten.Cleanup(target, evidence, installed.ReceiptSha256);
            if (File.ReadAllText(Path.Combine(target, "level0")) != "original-level" || File.Exists(Path.Combine(target, rpc)) || !File.Exists(Path.Combine(target, pos)) || !Directory.Exists(evidence))
                throw new Exception("Cleanup failed to preserve original or unrelated files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
