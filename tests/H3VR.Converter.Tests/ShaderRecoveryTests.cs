using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class ShaderRecoveryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("shader recovery rejects output aliased under game data through a symlink parent", SymlinkParentOverlap);
        yield return ("shader recovery rejects symlink children before probing exporter", SymlinkInputChild);
        yield return ("shader identities preserve signed path IDs and reject duplicates", ShaderIdentity);
    }

    private static void ShaderIdentity()
    {
        var ids = ShaderRecovery.ShaderPathIds(new long[] { 37, -22, 0, long.MaxValue }, "synthetic");
        Check(ids.SequenceEqual(new long[] { -22, 0, 37, long.MaxValue }), "identity IDs changed or were not sorted");
        var error = Capture(() => ShaderRecovery.ShaderPathIds(new long[] { 2, 2 }, "synthetic"));
        Check(error is InvalidDataException, "duplicate IDs were accepted");
    }

    private static void SymlinkParentOverlap()
    {
        using var temp = new Temp();
        var actual = Path.Combine(temp.Root, "actual");
        var game = Path.Combine(actual, "game");
        var gameData = Path.Combine(game, "h3vr_Data");
        Directory.CreateDirectory(gameData);
        var alias = Path.Combine(temp.Root, "alias");
        Directory.CreateSymbolicLink(alias, actual);
        var ripper = Path.Combine(temp.Root, "fake-ripper"); File.WriteAllText(ripper, "not launched");
        var output = Path.Combine(alias, "game", "h3vr_Data", "recovery");
        var error = Capture(() => ShaderRecovery.RecoverAsync(new ShaderRecoveryOptions(gameData, output, ripper)).GetAwaiter().GetResult());
        Check(error is not null && error.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase),
            "output reached the installed game through a symlink parent without being rejected");
    }

    private static void SymlinkInputChild()
    {
        using var temp = new Temp();
        var gameData = Path.Combine(temp.Root, "game-data"); Directory.CreateDirectory(gameData);
        var outside = Path.Combine(temp.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(gameData, "linked-assets"), outside);
        var ripper = Path.Combine(temp.Root, "fake-ripper"); File.WriteAllText(ripper, "not launched");
        var output = Path.Combine(temp.Root, "recovery");
        var error = Capture(() => ShaderRecovery.RecoverAsync(new ShaderRecoveryOptions(gameData, output, ripper)).GetAwaiter().GetResult());
        Check(error is InvalidDataException && error.Message.Contains("symlink", StringComparison.OrdinalIgnoreCase),
            "symlinked game-data child was not rejected before AssetRipper version probing");
        Check(!Directory.Exists(output), "rejected symlink input created an output tree");
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; } catch (Exception e) { return e; }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "h3vr-shader-recovery-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
