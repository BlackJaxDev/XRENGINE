using XREngine.Input;

namespace XREngine.Components.Animation;

public partial class VRIKSolverComponent
{
    private readonly float[] _trackingWeight = new float[11];
    private readonly float[] _trackingLostSeconds = new float[11];
    private readonly string?[] _boundIdentity = new string?[11];
    private readonly long[] _boundGeneration = new long[11];
    private readonly VrSlotPoseState[] _slotStates = new VrSlotPoseState[11];
    private float[] _trackingBaseWeights = new float[17];
    private float _headBasePositionWeight = 1, _headBaseRotationWeight = 1;
    private bool _hasHeadWeightBaseline;
    public float TrackingLossHoldSeconds { get; set; } = 0.1f;
    public float TrackingCrossfadeSeconds { get; set; } = 0.2f;

    public VrSlotPoseState GetSlotPoseState(EHumanoidIKTarget slot) => _slotStates[(int)slot];

    internal void RestoreConfiguredHeadWeights()
    {
        if (_hasHeadWeightBaseline)
        {
            Solver.Spine.PositionWeight = _headBasePositionWeight;
            Solver.Spine.RotationWeight = _headBaseRotationWeight;
        }
    }

    private void ResetTrackingWeights()
    {
        _trackingBaseWeights = CaptureCalibrationWeights();
        if (!_hasHeadWeightBaseline)
        {
            _headBasePositionWeight = Solver.Spine.PositionWeight;
            _headBaseRotationWeight = Solver.Spine.RotationWeight;
            _hasHeadWeightBaseline = true;
        }
        for (int i = 0; i < 11; i++)
        {
            bool bound = _calibrationTargets.TryGetValue((EHumanoidIKTarget)i, out var binding);
            var source = bound ? binding.Source as IVrTrackingPoseSource : null;
            _boundIdentity[i] = source?.TrackingIdentity;
            _boundGeneration[i] = source?.TrackingSessionGeneration ?? 0;
            _trackingLostSeconds[i] = 0;
            _trackingWeight[i] = bound ? 1 : 0;
            _slotStates[i] = new(bound ? EVrSlotPoseSource.Tracker : EVrSlotPoseSource.None, _trackingWeight[i], bound, false);
        }
    }

    /// <summary>Updates loss/recovery blending on the simulation owner, without discovery or allocations.</summary>
    public void UpdateTrackingWeights(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
            return;
        float hold = float.IsFinite(TrackingLossHoldSeconds) ? MathF.Max(0, TrackingLossHoldSeconds) : 0;
        float fade = float.IsFinite(TrackingCrossfadeSeconds) ? MathF.Max(0.001f, TrackingCrossfadeSeconds) : 0.2f;
        for (int i = 0; i < 11; i++)
        {
            if (!_calibrationTargets.TryGetValue((EHumanoidIKTarget)i, out var binding))
                continue;
            var source = binding.Source as IVrTrackingPoseSource;
            bool identityChanged = source is not null &&
                (!string.Equals(_boundIdentity[i], source.TrackingIdentity, StringComparison.Ordinal)
                    || _boundGeneration[i] != source.TrackingSessionGeneration);
            bool usable = !identityChanged && IsLive(binding.Source) && (source?.PoseCurrentlyUsable ?? true);
            float previousLost = _trackingLostSeconds[i];
            _trackingLostSeconds[i] = usable ? 0 : previousLost + deltaSeconds;
            if (identityChanged)
                _trackingWeight[i] = 0;
            else if (usable)
                _trackingWeight[i] = MathF.Min(1, _trackingWeight[i] + deltaSeconds / fade);
            else
            {
                float fadingTime = MathF.Max(0, _trackingLostSeconds[i] - hold) - MathF.Max(0, previousLost - hold);
                _trackingWeight[i] = MathF.Max(0, _trackingWeight[i] - fadingTime / fade);
            }
            _slotStates[i] = new(_trackingWeight[i] > 0 ? EVrSlotPoseSource.Tracker : EVrSlotPoseSource.None,
                _trackingWeight[i], usable, identityChanged);
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.Head))
        {
            Solver.Spine.PositionWeight = _headBasePositionWeight * _trackingWeight[0];
            Solver.Spine.RotationWeight = _headBaseRotationWeight * _trackingWeight[0];
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.Hips))
        {
            Solver.Spine.HipsPositionWeight = _trackingBaseWeights[0] * _trackingWeight[1];
            Solver.Spine.HipsRotationWeight = _trackingBaseWeights[1] * _trackingWeight[1];
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.Chest))
            Solver.Spine.ChestTargetWeight = _trackingBaseWeights[2] * _trackingWeight[10];
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.LeftHand))
        {
            Solver.LeftArm.Settings.PositionWeight = _trackingBaseWeights[3] * _trackingWeight[2];
            Solver.LeftArm.Settings.RotationWeight = _trackingBaseWeights[4] * _trackingWeight[2];
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.LeftElbow))
            Solver.LeftArm.Settings.UpperArmTargetWeight = _trackingBaseWeights[5] * _trackingWeight[6];
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.RightHand))
        {
            Solver.RightArm.Settings.PositionWeight = _trackingBaseWeights[6] * _trackingWeight[3];
            Solver.RightArm.Settings.RotationWeight = _trackingBaseWeights[7] * _trackingWeight[3];
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.RightElbow))
            Solver.RightArm.Settings.UpperArmTargetWeight = _trackingBaseWeights[8] * _trackingWeight[7];
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.LeftFoot))
        {
            Solver.LeftLeg.PositionWeight = _trackingBaseWeights[9] * _trackingWeight[4];
            Solver.LeftLeg.RotationWeight = _trackingBaseWeights[10] * _trackingWeight[4];
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.LeftKnee))
            Solver.LeftLeg.KneeTargetWeight = _trackingBaseWeights[11] * _trackingWeight[8];
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.RightFoot))
        {
            Solver.RightLeg.PositionWeight = _trackingBaseWeights[12] * _trackingWeight[5];
            Solver.RightLeg.RotationWeight = _trackingBaseWeights[13] * _trackingWeight[5];
        }
        if (_calibrationTargets.ContainsKey(EHumanoidIKTarget.RightKnee))
            Solver.RightLeg.KneeTargetWeight = _trackingBaseWeights[14] * _trackingWeight[9];
    }
}
