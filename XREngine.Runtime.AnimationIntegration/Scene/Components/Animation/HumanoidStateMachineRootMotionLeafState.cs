using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Avatar-dependent cache and current result for one persistent graph-leaf occurrence.
/// </summary>
internal sealed class HumanoidStateMachineRootMotionLeafState
{
    private readonly UnityHumanoidClipRootMotionSettings _effectiveSettings = new();
    private readonly UnityHumanoidMuscleSampleBuffer _canonicalProjectionMuscles = new();
    private readonly UnityHumanoidMuscleSampleBuffer _loopStartMuscles = new();
    private readonly UnityHumanoidMuscleSampleBuffer _loopEndMuscles = new();
    private readonly UnityHumanoidMuscleSampleBuffer _currentProjectionMuscles = new();
    private readonly float[] _mirrorScratch = new float[(int)EHumanoidValue.RightHandThumb3Stretched + 1];

    private AnimationClip? _clip;
    private UnityHumanoidRootMotionPolicy _policy;
    private HumanoidImportedBodySample _canonicalBody = HumanoidImportedBodySample.Neutral;
    private HumanoidProjectedRootPose _loopGenerator = HumanoidProjectedRootPose.Identity;
    private HumanoidLoopPoseCorrection _loopPoseCorrection = HumanoidLoopPoseCorrection.Identity;
    private ulong _lifecycleGeneration;
    private bool _isAssigned;
    private bool _cacheValid;
    private bool _hasLoopGenerator;
    private bool _hasCanonicalFeetY;
    private float _canonicalFeetY;

    public ulong OccurrenceId { get; private set; }
    public bool IsAssigned => _isAssigned;
    public float Weight { get; private set; }
    public long SourceLoopCycle { get; private set; }
    public EUnityHumanoidMotionContributionType ContributionType { get; private set; }
    public UnityHumanoidRootMotionPolicy Policy => _policy;
    public HumanoidImportedBodySample CanonicalBody => _canonicalBody;
    public HumanoidImportedBodySample CurrentBody { get; private set; } = HumanoidImportedBodySample.Neutral;
    public HumanoidProjectedRootPose BodyAllocationProjectedRootPose { get; private set; } = HumanoidProjectedRootPose.Identity;
    public HumanoidProjectedRootPose UnwrappedProjectedRootPose { get; private set; } = HumanoidProjectedRootPose.Identity;
    public HumanoidLoopPoseCorrection CurrentLoopPoseCorrection { get; private set; } = HumanoidLoopPoseCorrection.Identity;
    public ReadOnlySpan<float> CanonicalProjectionMuscles => _canonicalProjectionMuscles.Values;
    public ReadOnlySpan<float> CurrentProjectionMuscles => _currentProjectionMuscles.Values;

    public bool Matches(ulong occurrenceId, ulong lifecycleGeneration)
        => _isAssigned
        && OccurrenceId == occurrenceId
        && _lifecycleGeneration == lifecycleGeneration;

    public bool TryPrepare(
        in UnityHumanoidMotionContribution contribution,
        HumanoidComponent humanoid)
    {
        bool identityChanged = !_isAssigned
            || OccurrenceId != contribution.OccurrenceId
            || !ReferenceEquals(_clip, contribution.Clip)
            || _policy != contribution.Policy;
        if (identityChanged)
        {
            _isAssigned = true;
            OccurrenceId = contribution.OccurrenceId;
            _clip = contribution.Clip;
            _policy = contribution.Policy;
            CopyPolicyToSettings(_policy, _effectiveSettings);
            _cacheValid = CacheCanonicalAndLoop(humanoid);
            _hasCanonicalFeetY = false;
        }

        if (_lifecycleGeneration != contribution.LifecycleGeneration)
        {
            _lifecycleGeneration = contribution.LifecycleGeneration;
            _hasCanonicalFeetY = false;
        }

        Weight = contribution.Weight;
        SourceLoopCycle = contribution.SourceLoopCycle;
        ContributionType = contribution.ContributionType;
        if (!_cacheValid || _clip is null
            || !_clip.TrySampleUnityHumanoidBody(
                contribution.SampleTime,
                out Vector3 currentPosition,
                out Quaternion currentRotation))
            return false;

        CurrentBody = CreateImportedBodySample(currentPosition, currentRotation);
        _currentProjectionMuscles.Clear();
        _clip.PublishUnityHumanoidProjectionMusclesAtTime(
            contribution.SampleTime,
            _currentProjectionMuscles);
        ApplyInheritedMirrorIfNeeded(_currentProjectionMuscles.Values);

        HumanoidProjectedRootPose withinCycle = humanoid.CalculateProjectedRootPose(
            CurrentBody,
            _canonicalBody,
            1.0f,
            _effectiveSettings,
            projectionCalibrationClipName: null,
            _currentProjectionMuscles.Values);
        BodyAllocationProjectedRootPose = withinCycle;
        UnwrappedProjectedRootPose = contribution.SourceLoopCycle != 0L && _hasLoopGenerator
            ? HumanoidComponent.ComposeProjectedRootPoses(
                PowProjectedRootPose(_loopGenerator, contribution.SourceLoopCycle),
                withinCycle)
            : withinCycle;
        CurrentLoopPoseCorrection = _policy.LoopPose
            ? _loopPoseCorrection.AtPhase(contribution.SamplePhase)
            : HumanoidLoopPoseCorrection.Identity;
        return true;
    }

    public void AddProjectedFeetDelta(float deltaY)
    {
        if (!float.IsFinite(deltaY))
            return;

        BodyAllocationProjectedRootPose = AddProjectedY(BodyAllocationProjectedRootPose, deltaY);
        UnwrappedProjectedRootPose = AddProjectedY(UnwrappedProjectedRootPose, deltaY);
    }

    public bool TryGetCanonicalFeetY(out float value)
    {
        value = _canonicalFeetY;
        return _hasCanonicalFeetY;
    }

    public void SetCanonicalFeetY(float value)
    {
        if (!float.IsFinite(value))
            return;

        _canonicalFeetY = value;
        _hasCanonicalFeetY = true;
    }

    private bool CacheCanonicalAndLoop(HumanoidComponent humanoid)
    {
        _canonicalBody = HumanoidImportedBodySample.Neutral;
        _loopGenerator = HumanoidProjectedRootPose.Identity;
        _loopPoseCorrection = HumanoidLoopPoseCorrection.Identity;
        _hasLoopGenerator = false;
        _canonicalProjectionMuscles.Clear();
        _loopStartMuscles.Clear();
        _loopEndMuscles.Clear();
        if (_clip is null
            || !_clip.TrySampleUnityHumanoidBody(0.0f, out Vector3 startPosition, out Quaternion startRotation)
            || !_clip.TrySampleUnityHumanoidBody(_clip.LengthInSeconds, out Vector3 endPosition, out Quaternion endRotation))
            return false;

        HumanoidImportedBodySample sourceStart = CreateImportedBodySample(startPosition, startRotation);
        HumanoidImportedBodySample sourceEnd = CreateImportedBodySample(endPosition, endRotation);
        float offsetSeconds = _policy.NormalizedCycleOffset * _clip.LengthInSeconds;
        HumanoidImportedBodySample offsetSample = sourceStart;
        if (offsetSeconds > 0.0f)
        {
            if (!_clip.TrySampleUnityHumanoidBody(
                    offsetSeconds,
                    out Vector3 offsetPosition,
                    out Quaternion offsetRotation))
                return false;

            offsetSample = CreateImportedBodySample(offsetPosition, offsetRotation);
        }

        // Playback time zero samples the cycle-offset source phase. Keep the same
        // canonical Body reference as direct playback; sourceStart remains the
        // endpoint reference used to derive one temporal loop generator.
        _canonicalBody = offsetSample;
        _clip.PublishUnityHumanoidProjectionMusclesAtTime(
            offsetSeconds,
            _canonicalProjectionMuscles);
        _clip.PublishUnityHumanoidProjectionMusclesAtTime(0.0f, _loopStartMuscles);
        _clip.PublishUnityHumanoidProjectionMusclesAtTime(_clip.LengthInSeconds, _loopEndMuscles);
        ApplyInheritedMirrorIfNeeded(_canonicalProjectionMuscles.Values);
        ApplyInheritedMirrorIfNeeded(_loopStartMuscles.Values);
        ApplyInheritedMirrorIfNeeded(_loopEndMuscles.Values);

        humanoid.CalculateLoopEvaluation(
            sourceStart,
            sourceEnd,
            _loopStartMuscles.Values,
            _loopEndMuscles.Values,
            1.0f,
            _effectiveSettings,
            projectionCalibrationClipName: null,
            out _loopPoseCorrection,
            out HumanoidProjectedRootPose sourceGenerator);
        if (!_policy.LoopTime)
            return true;

        if (offsetSeconds > 0.0f)
        {
            HumanoidProjectedRootPose offsetFromStart = humanoid.CalculateProjectedRootPose(
                offsetSample,
                sourceStart,
                1.0f,
                _effectiveSettings,
                projectionCalibrationClipName: null,
                ReadOnlySpan<float>.Empty);
            _loopGenerator = HumanoidComponent.ComposeProjectedRootPoses(
                HumanoidComponent.InvertProjectedRootPose(offsetFromStart),
                HumanoidComponent.ComposeProjectedRootPoses(sourceGenerator, offsetFromStart));
        }
        else
        {
            _loopGenerator = sourceGenerator;
        }

        _hasLoopGenerator = _loopGenerator.Channels != EHumanoidProjectedRootChannels.None;
        return true;
    }

    private void ApplyInheritedMirrorIfNeeded(float[] values)
    {
        bool sourceMirror = _clip?.UnityHumanoidRootMotionSettings?.Mirror == true;
        if (sourceMirror == _policy.Mirror)
            return;

        Array.Clear(_mirrorScratch);
        int count = Math.Min(values.Length, _mirrorScratch.Length);
        for (int sourceIndex = 0; sourceIndex < count; sourceIndex++)
        {
            EHumanoidValue source = (EHumanoidValue)sourceIndex;
            EHumanoidValue mirrored = UnityHumanoidMirrorOperator.MirrorMuscle(source, out float parity);
            int mirroredIndex = (int)mirrored;
            if ((uint)mirroredIndex < (uint)_mirrorScratch.Length)
                _mirrorScratch[mirroredIndex] = values[sourceIndex] * parity;
        }
        _mirrorScratch.AsSpan(0, count).CopyTo(values);
    }

    private static HumanoidProjectedRootPose AddProjectedY(HumanoidProjectedRootPose pose, float deltaY)
    {
        Vector3 position = pose.Position;
        position.Y += deltaY;
        return new HumanoidProjectedRootPose(
            position,
            pose.Rotation,
            pose.Channels | EHumanoidProjectedRootChannels.PositionY);
    }

    private static HumanoidImportedBodySample CreateImportedBodySample(Vector3 position, Quaternion rotation)
        => new()
        {
            Position = position,
            Rotation = rotation,
            Channels = EHumanoidImportedBodySampleChannels.All,
        };

    private static void CopyPolicyToSettings(
        UnityHumanoidRootMotionPolicy policy,
        UnityHumanoidClipRootMotionSettings settings)
    {
        settings.StartTime = policy.StartTime;
        settings.StopTime = policy.StopTime;
        settings.OrientationOffsetY = policy.OrientationOffsetY;
        settings.Level = policy.Level;
        settings.CycleOffset = policy.CycleOffset;
        settings.LoopTime = policy.LoopTime;
        settings.LoopPose = policy.LoopPose;
        settings.BakeOrientationIntoPose = policy.BakeOrientationIntoPose;
        settings.BakePositionYIntoPose = policy.BakePositionYIntoPose;
        settings.BakePositionXZIntoPose = policy.BakePositionXZIntoPose;
        settings.KeepOriginalOrientation = policy.OrientationBasis is EUnityHumanoidRootOrientationBasis.Original;
        settings.KeepOriginalPositionY = policy.PositionYBasis is EUnityHumanoidRootPositionYBasis.Original;
        settings.KeepOriginalPositionXZ = policy.PositionXZBasis is EUnityHumanoidRootPositionXZBasis.Original;
        settings.HeightFromFeet = policy.PositionYBasis is EUnityHumanoidRootPositionYBasis.Feet;
        settings.Mirror = policy.Mirror;
    }

    private static HumanoidProjectedRootPose PowProjectedRootPose(
        HumanoidProjectedRootPose pose,
        long exponent)
    {
        if (exponent == 0L)
            return new HumanoidProjectedRootPose(Vector3.Zero, Quaternion.Identity, pose.Channels);

        HumanoidProjectedRootPose factor = exponent < 0L
            ? HumanoidComponent.InvertProjectedRootPose(pose)
            : pose;
        ulong remaining = exponent < 0L
            ? unchecked((ulong)(-(exponent + 1L))) + 1UL
            : (ulong)exponent;
        HumanoidProjectedRootPose result = new(Vector3.Zero, Quaternion.Identity, pose.Channels);
        while (remaining != 0UL)
        {
            if ((remaining & 1UL) != 0UL)
                result = HumanoidComponent.ComposeProjectedRootPoses(result, factor);
            remaining >>= 1;
            if (remaining != 0UL)
                factor = HumanoidComponent.ComposeProjectedRootPoses(factor, factor);
        }
        return result;
    }
}
