using System.Numerics;

namespace XREngine.Components.VR;

/// <summary>A current physical tracker sample; identities have no body-role meaning.</summary>
public readonly record struct VrTrackerBindingCandidate(string Identity, Vector3 Position, bool Usable);
