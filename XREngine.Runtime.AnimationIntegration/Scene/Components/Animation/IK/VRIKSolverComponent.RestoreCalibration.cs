using System.Numerics;

namespace XREngine.Components.Animation;

public partial class VRIKSolverComponent
{
    public VrCalibrationResult RestoreCalibration(ReadOnlySpan<VrCalibrationTarget> targets, object? settings)
    {
        if (settings is not VRIKCalibrationSettings calibrationSettings || !Solver.Initialized)
            return VrCalibrationResult.Failure("Avatar calibration is not ready to restore.");
        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (!IsLive(target.Source) || !VrCalibrationMath.IsFinite(target.Offset) || !Matrix4x4.Invert(target.Offset, out _))
                return VrCalibrationResult.Failure("Stored calibration contains an invalid source or offset.");
            for (int j = 0; j < i; j++)
                if (targets[j].Slot == target.Slot || ReferenceEquals(targets[j].Source, target.Source))
                    return VrCalibrationResult.Failure("Stored calibration contains duplicate bindings.");
        }
        try
        {
            CommitCalibrationTargets(targets, () => VRIKCalibrator.ApplyWeights(this, calibrationSettings));
            return VrCalibrationResult.Completed(targets.ToArray());
        }
        catch (Exception exception)
        {
            return VrCalibrationResult.Failure("Stored calibration could not be restored: " + exception.Message);
        }
    }
}
