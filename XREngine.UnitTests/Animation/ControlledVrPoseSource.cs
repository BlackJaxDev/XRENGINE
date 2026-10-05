using System.Numerics;
using XREngine.Data.Components.Scene;
using XREngine.Input;

namespace XREngine.UnitTests.Animation;

/// <summary>Controlled runtime pose for exercising the production transform retention path.</summary>
internal sealed class ControlledVrPoseSource : VRDeviceTransformBase
{
    public bool CurrentValid { get; set; }
    public Matrix4x4 CurrentPose { get; set; }
    public override RuntimeVrDeviceInfo? Device => null;
    public Matrix4x4 Evaluate() => base.CreateLocalMatrix();
    public override bool TryGetCurrentLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        pose = CurrentPose;
        return CurrentValid;
    }
}
