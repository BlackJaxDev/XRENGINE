namespace XREngine.Components.Animation;

/// <summary>Exact mutable solver settings preserved across a failed calibration publication.</summary>
internal sealed class VRIKSolverWeightSnapshot
{
    private readonly float _headPosition, _headRotation, _hipsPosition, _hipsRotation, _chestGoal, _minHeadHeight;
    private readonly float _leftHandPosition, _leftHandRotation, _leftBend, _leftUpperArm;
    private readonly float _rightHandPosition, _rightHandRotation, _rightBend, _rightUpperArm;
    private readonly float _leftFootPosition, _leftFootRotation, _leftKnee;
    private readonly float _rightFootPosition, _rightFootRotation, _rightKnee;
    private readonly bool _plantFeet;

    public VRIKSolverWeightSnapshot(IKSolverVR solver)
    {
        _headPosition = solver.Spine.PositionWeight;
        _headRotation = solver.Spine.RotationWeight;
        _hipsPosition = solver.Spine.HipsPositionWeight;
        _hipsRotation = solver.Spine.HipsRotationWeight;
        _chestGoal = solver.Spine.ChestGoalWeight;
        _minHeadHeight = solver.Spine.MinHeadHeight;
        _leftHandPosition = solver.LeftArm.Settings.PositionWeight;
        _leftHandRotation = solver.LeftArm.Settings.RotationWeight;
        _leftBend = solver.LeftArm.Settings.BendGoalWeight;
        _leftUpperArm = solver.LeftArm.UpperArmGoalWeight;
        _rightHandPosition = solver.RightArm.Settings.PositionWeight;
        _rightHandRotation = solver.RightArm.Settings.RotationWeight;
        _rightBend = solver.RightArm.Settings.BendGoalWeight;
        _rightUpperArm = solver.RightArm.UpperArmGoalWeight;
        _leftFootPosition = solver.LeftLeg.PositionWeight;
        _leftFootRotation = solver.LeftLeg.RotationWeight;
        _leftKnee = solver.LeftLeg.KneeTargetWeight;
        _rightFootPosition = solver.RightLeg.PositionWeight;
        _rightFootRotation = solver.RightLeg.RotationWeight;
        _rightKnee = solver.RightLeg.KneeTargetWeight;
        _plantFeet = solver.PlantFeet;
    }

    public void Restore(IKSolverVR solver)
    {
        solver.Spine.PositionWeight = _headPosition;
        solver.Spine.RotationWeight = _headRotation;
        solver.Spine.HipsPositionWeight = _hipsPosition;
        solver.Spine.HipsRotationWeight = _hipsRotation;
        solver.Spine.ChestGoalWeight = _chestGoal;
        solver.Spine.MinHeadHeight = _minHeadHeight;
        solver.LeftArm.Settings.PositionWeight = _leftHandPosition;
        solver.LeftArm.Settings.RotationWeight = _leftHandRotation;
        solver.LeftArm.Settings.BendGoalWeight = _leftBend;
        solver.LeftArm.UpperArmGoalWeight = _leftUpperArm;
        solver.RightArm.Settings.PositionWeight = _rightHandPosition;
        solver.RightArm.Settings.RotationWeight = _rightHandRotation;
        solver.RightArm.Settings.BendGoalWeight = _rightBend;
        solver.RightArm.UpperArmGoalWeight = _rightUpperArm;
        solver.LeftLeg.PositionWeight = _leftFootPosition;
        solver.LeftLeg.RotationWeight = _leftFootRotation;
        solver.LeftLeg.KneeTargetWeight = _leftKnee;
        solver.RightLeg.PositionWeight = _rightFootPosition;
        solver.RightLeg.RotationWeight = _rightFootRotation;
        solver.RightLeg.KneeTargetWeight = _rightKnee;
        solver.PlantFeet = _plantFeet;
    }
}
