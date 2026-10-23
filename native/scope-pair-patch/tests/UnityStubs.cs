// Host-only lifecycle model; these are never compiled into the player.
namespace UnityEngine {
public class Object {
    public bool Destroyed;
    public static bool operator ==(Object a, Object b) {
        bool an = ReferenceEquals(a, null) || a.Destroyed,
             bn = ReferenceEquals(b, null) || b.Destroyed;
        return an || bn ? an == bn : ReferenceEquals(a, b);
    }
    public static bool operator !=(Object a, Object b) { return !(a == b); }
    public override bool Equals(object value) { return ReferenceEquals(this, value); }
    public override int GetHashCode() {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}
public class Behaviour : Object {
    bool current;
    public int Writes, Creates, Releases;
    public bool ThrowOnDisable;
    public Behaviour(bool initial = false) { current = initial; }
    public bool enabled {
        get { return current; }
        set {
            if (Destroyed)
                throw new InvalidOperationException("Destroyed behaviour setter");
            Writes++;
            if (current == value)
                return;
            current = value;
            if (value)
                Creates++;
            else
                Releases++;
            if (!value && ThrowOnDisable)
                throw new InvalidOperationException("OnDisable failed");
        }
    }
}
}
