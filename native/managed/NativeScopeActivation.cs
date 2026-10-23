#if NEWENGINE_UNITY_2021
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeH3VR {
// Graphics resource lifetime only: keep a scope selected by either eye
// active until the pair ends. Original visibility and draw decisions remain.
public static class NewEngineScopeActivation {
    static readonly bool enabled = ReadEnabled();
    static readonly List<Behaviour> selected = new List<Behaviour>(8);
    static readonly List<Behaviour> pending = new List<Behaviour>(8);
    static bool active;
    public static bool Enabled {
        get { return enabled; }
    }
    public static bool PairActive {
        get { return active; }
    }
    static bool ReadEnabled() {
        string value = Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_PAIR_FIX");
        if (string.IsNullOrEmpty(value) || value == "1")
            return true;
        if (value == "0")
            return false;
        throw new InvalidOperationException(
            "NEWENGINEH3VR_SCOPE_PAIR_FIX must be 0 or 1 (unset defaults to 1)");
    }
    public static string Describe() {
        return enabled ? "scope lifetime: selected-by-either-eye pair activation"
                       : "scope lifetime: original per-eye activation (diagnostic baseline)";
    }
    static bool Contains(List<Behaviour> values, Behaviour value) {
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], value))
                return true;
        return false;
    }
    public static void BeginPair() {
        if (!enabled)
            return;
        if (active)
            throw new InvalidOperationException("Nested native scope eye pair");
        selected.Clear();
        pending.Clear();
        active = true;
    }
    public static void SetEnabled(Behaviour scope, bool value) {
        if (!enabled || !active) {
            scope.enabled = value;
            return;
        }
        if (value) {
            scope.enabled = true;
            if (!Contains(selected, scope))
                selected.Add(scope);
        } else if (!Contains(selected, scope) && !Contains(pending, scope))
            pending.Add(scope);
    }
    public static void EndPair() {
        if (!enabled || !active)
            return;
        active = false;
        Exception failure = null;
        try {
            for (int i = 0; i < pending.Count; i++) {
                Behaviour scope = pending[i];
                if (scope == null || Contains(selected, scope))
                    continue;
                try {
                    scope.enabled = false;
                } catch (Exception error) {
                    if (failure == null)
                        failure = error;
                }
            }
        } finally {
            selected.Clear();
            pending.Clear();
        }
        if (failure != null)
            throw failure;
    }
}
}
#endif
