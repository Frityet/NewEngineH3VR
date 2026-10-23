public static class Trace {
    public static List<string> Calls = new();
}
namespace UnityEngine {
public class Object {
    public bool Destroyed;
    public static bool operator ==(Object? a,
                                   Object? b) => (ReferenceEquals(a, null) || a.Destroyed) ==
                                                 (ReferenceEquals(b, null) || b.Destroyed);
    public static bool operator !=(Object? a, Object? b) => !(a == b);
    public override bool Equals(object? value) => ReferenceEquals(this, value);
    public override int GetHashCode() => base.GetHashCode();
    public static void Destroy(Object value) => Trace.Calls.Add("deferred");
    public static void DestroyImmediate(Object value, bool allowDestroyingAssets) =>
        Trace.Calls.Add("immediate:" + allowDestroyingAssets);
}
public static class Application {
    public static bool Editor, Playing;
    public static bool isEditor {
        get {
            Trace.Calls.Add("isEditor");
            return Editor;
        }
    }
    public static bool isPlaying {
        get {
            Trace.Calls.Add("isPlaying");
            return Playing;
        }
    }
}
}
namespace UnityEngine.Rendering.PostProcessing {
public static class RuntimeUtilities {
    public static void Destroy(UnityEngine.Object? obj) {
        if (obj != null)
            UnityEngine.Object.Destroy(obj!);
    }
}
}
public static class Program {
    public static void Main(string[] args) {
        bool patched = args.Contains("patched");
        int cases = 0;
        foreach (bool editor in new[] { false, true })
            foreach (bool playing in new[] { false, true })
                foreach (int state in new[] { 0, 1, 2 }) {
                    UnityEngine.Application.Editor = editor;
                    UnityEngine.Application.Playing = playing;
                    Trace.Calls.Clear();
                    var obj = state == 0 ? null : new UnityEngine.Object { Destroyed = state == 2 };
                    UnityEngine.Rendering.PostProcessing.RuntimeUtilities.Destroy(obj);
                    string[] expected = state != 1 ? Array.Empty<string>()
                                        : !patched ? new[] { "deferred" }
                                        : !editor  ? new[] { "isEditor", "deferred" }
                                        : playing
                                            ? new[] { "isEditor", "isPlaying", "deferred" }
                                            : new[] { "isEditor", "isPlaying", "immediate:False" };
                    if (!Trace.Calls.SequenceEqual(expected))
                        throw new Exception(
                            $"editor={editor} playing={playing} objectState={state}: " +
                            string.Join(",", Trace.Calls));
                    cases++;
                }
        Console.WriteLine(
            patched
                ? $"PASS {cases} patched cases: player both states and editor play deferred; edit-only immediate; null/destroyed no calls; assets not permitted"
                : $"PASS {cases} baseline cases: original deferred call for every non-null object; null/destroyed no calls");
    }
}
