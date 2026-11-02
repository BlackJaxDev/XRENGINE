using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Stores the local rotation of one transform in an authored avatar T-pose.
/// </summary>
/// <param name="Name">The transform name.</param>
/// <param name="ParentName">The parent transform name. The pair identifies the transform.</param>
/// <param name="Rotation">The local rotation in engine coordinates.</param>
public readonly record struct HumanoidAuthoredTPoseBone(string Name, string ParentName, Quaternion Rotation);
