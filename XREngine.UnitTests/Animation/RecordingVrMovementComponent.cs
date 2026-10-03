using System.Numerics;
using XREngine.Components;
using XREngine.Components.Movement;

namespace XREngine.UnitTests.Animation;

public sealed class RecordingVrMovementComponent : XRComponent, IRuntimeCharacterMovementComponent
{
    public float StandingHeight { get; set; }
    public float CrouchedHeight { get; set; }
    public float ProneHeight { get; set; }
    public float Radius { get; set; }
    public float HalfHeight => StandingHeight / 2;
    public Vector3 LastInput { get; private set; }
    public void AddLiteralInputDelta(Vector3 offset) => LastInput = offset;
}
