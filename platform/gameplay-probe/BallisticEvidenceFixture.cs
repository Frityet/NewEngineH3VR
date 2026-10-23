using System;
internal static class BallisticEvidenceFixture {
    static int checks;
    static void Need(bool condition, string label) {
        checks++;
        if (!condition)
            throw new Exception(label);
    }
    static void Main() {
        Need(NativeGameplayBallisticEvidence.Accept(true, .005f, 250f, 1.5f),
             "genuine fired/travelled evidence");
        Need(!NativeGameplayBallisticEvidence.Accept(false, .005f, 250f, 1.5f),
             "unmatched old/unrelated projectile");
        Need(!NativeGameplayBallisticEvidence.Accept(true, 1f, 250f, 1.5f),
             "projectile from another muzzle");
        Need(!NativeGameplayBallisticEvidence.Accept(true, .005f, 0, 1.5f),
             "instantiated but never fired");
        Need(!NativeGameplayBallisticEvidence.Accept(true, .005f, 250f, 0),
             "shot event without trajectory progress");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                                          -1f }) {
            Need(!NativeGameplayBallisticEvidence.Accept(true, invalid, 250f, 1.5f),
                 "invalid origin");
            Need(!NativeGameplayBallisticEvidence.Accept(true, .005f, invalid, 1.5f),
                 "invalid velocity");
            Need(!NativeGameplayBallisticEvidence.Accept(true, .005f, 250f, invalid),
                 "invalid travel");
        }
        Console.WriteLine(
            "PASS " + checks +
            " ballistic acceptance predicate checks; fixture is not actual game firing proof.");
    }
}
