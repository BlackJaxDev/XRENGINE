using System.Numerics;

namespace XREngine.Components.Movement;

public interface IRuntimeCharacterMovementComponent
{
    float StandingHeight { get; set; }
    float CrouchedHeight { get; set; }
    float ProneHeight { get; set; }
    float Radius { get; set; }
    float HalfHeight { get; }
    void AddLiteralInputDelta(Vector3 offset);
}