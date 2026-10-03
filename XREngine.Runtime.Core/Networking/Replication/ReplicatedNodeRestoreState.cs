using System.Numerics;
using XREngine.Scene;

namespace XREngine.Networking;

/// <summary>Local package state retained for deterministic leave and instance switching.</summary>
internal sealed record ReplicatedNodeRestoreState(SceneNode Node, SceneNode? Parent, string? Name, bool Active,
    Vector3 Translation, Quaternion Rotation, Vector3 Scale);
