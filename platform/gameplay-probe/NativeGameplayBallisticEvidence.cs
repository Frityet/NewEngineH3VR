using System;

// Pure acceptance predicate shared with the negative fixture. The caller must
// establish newly instantiated, exact prefab identity before passing matched.
internal static class NativeGameplayBallisticEvidence {
    internal static bool Accept(bool matched, float originError, float initialVelocity,
                                float travelled) {
        return matched && Finite(originError) && originError >= 0 && originError < .1f &&
               Finite(initialVelocity) && initialVelocity > 0 && Finite(travelled) &&
               travelled > .001f;
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
}
