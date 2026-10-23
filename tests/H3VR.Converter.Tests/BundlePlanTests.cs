using H3VR.Converter;
using System.Text.Json;

namespace H3VR.Converter.Tests;

public static class BundlePlanTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("original bundle address maps by case-insensitive exact path", ExactPath);
        yield return ("original bundle address permits unique bundle basename only", UniqueBundleBasename);
        yield return ("ambiguous bundle basename remains unresolved", AmbiguousBundleBasename);
        yield return ("bundle planner records parse failure and preserves source bytes", ReadOnlyFailurePlan);
    }

    private static void ExactPath()
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Assets/Objects/Gun.prefab"] = "Assets/Objects/Gun.prefab" };
        var members = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["bundle"] = ["Assets/Objects/Gun.prefab"] };
        var resolved = BundlePlan.Resolve("./assets/objects/gun.prefab", "bundle", assets, members);
        Check(resolved.Success && resolved.Asset == "Assets/Objects/Gun.prefab" && resolved.Resolution == "original-path", "exact address did not resolve to the canonical project asset path");
    }

    private static void UniqueBundleBasename()
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["bundle"] = ["Assets/Objects/Unique.prefab"] };
        var resolved = BundlePlan.Resolve("objects/Unique.prefab", "bundle", assets, members);
        Check(resolved.Success && resolved.Resolution == "unique-bundle-basename", "unique same-bundle fallback did not resolve");
    }

    private static void AmbiguousBundleBasename()
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["bundle"] = ["Assets/A/Shared.prefab", "Assets/B/Shared.prefab"] };
        var resolved = BundlePlan.Resolve("Shared.prefab", "bundle", assets, members);
        Check(!resolved.Success && resolved.Resolution == "ambiguous" && resolved.Candidates.Length == 2, "ambiguous matching basenames must fail closed");
    }

    private static void ReadOnlyFailurePlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "h3vr-bundle-plan-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var project = Path.Combine(root, "project");
        Directory.CreateDirectory(source); Directory.CreateDirectory(Path.Combine(project, "Assets"));
        try
        {
            var invalidBundle = Path.Combine(source, "not-a-valid-bundle"); File.WriteAllBytes(invalidBundle, "UnityFS"u8.ToArray());
            var before = SafeFiles.Hash(invalidBundle); var planPath = Path.Combine(root, "plan.json");
            var report = BundlePlan.Create(source, project, planPath);
            var plan = JsonDocument.Parse(File.ReadAllText(planPath)).RootElement;
            Check(SafeFiles.Hash(invalidBundle) == before, "bundle planning modified input bundle");
            Check(report.Bundles == 0 && report.Issues > 0 && plan.GetProperty("schemaVersion").GetInt32() == 1, "invalid UnityFS input did not produce an issue-bearing schema-1 plan");
            Check(plan.GetProperty("issues").GetArrayLength() > 0, "parse failure was lost from plan");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
