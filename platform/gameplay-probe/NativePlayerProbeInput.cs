using UnityEngine;

// World-space raw controller target at the instant of the call. The owner
// stores the resulting tracking-local pose so game locomotion carries it.
public delegate void NativePlayerHandDriver(uint role, Vector3 worldPosition,
                                            Quaternion worldRotation, float trigger, float grip,
                                            uint buttons, uint touches, Vector2 primaryAxes,
                                            Vector2 secondaryAxes);
