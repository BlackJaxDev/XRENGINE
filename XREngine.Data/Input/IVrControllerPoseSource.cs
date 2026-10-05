using System.Numerics;

namespace XREngine.Input;

/// <summary>Profile-aware grip-to-wrist geometry; it does not depend on capture hand placement.</summary>
public interface IVrControllerPoseSource
{
    string? InteractionProfile { get; }
    Vector3 GripToWristOffset { get; }
}
