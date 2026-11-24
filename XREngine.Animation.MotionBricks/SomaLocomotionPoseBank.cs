using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Holds preallocated SOMA 4-pose target templates for 1D linear locomotion (idle, walk, jog, run),
/// 2D directional strafing (idle, forward, backward, left, right), crouch and prone styles, and
/// jump, airborne, and landing poses without heap allocations.
/// </summary>
public sealed partial class SomaLocomotionPoseBank
{
    public const int StandingStyleIndex = (int)ELocomotionStyle.Standing;
    public const int CrouchedStyleIndex = (int)ELocomotionStyle.Crouched;
    public const int ProneStyleIndex = (int)ELocomotionStyle.Prone;

    private const int JointCount = SomaModelContract.JointCount;
    private const int TemplateFrames = SomaModelContract.TargetPoseCapacity;
    private const int TemplateSize = TemplateFrames * JointCount;

    private readonly SomaPoseTransform[] _idleTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _walkTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _jogTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _runTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _strafeForwardTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _backwardTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _strafeLeftTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _strafeRightTemplate = new SomaPoseTransform[TemplateSize];

    private readonly SomaPoseTransform[] _crouchIdleTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _crouchWalkTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _crouchBackwardTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _crouchStrafeLeftTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _crouchStrafeRightTemplate = new SomaPoseTransform[TemplateSize];

    private readonly SomaPoseTransform[] _proneIdleTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _proneMoveTemplate = new SomaPoseTransform[TemplateSize];

    private readonly SomaPoseTransform[] _jumpAscentPose = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _jumpFallPose = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _landingPose = new SomaPoseTransform[JointCount];

    private readonly SomaPoseTransform[] _scratchA = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _scratchB = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _transitionPoseA = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _transitionPoseB = new SomaPoseTransform[JointCount];
    private readonly SomaRestSkeletonCalibration _calibration;
    private readonly HumanoidToSomaPoseSampler _runtimeSampler;
    private readonly float[] _runtimeMuscleScratch = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
    private readonly SomaMotionClip[] _standingIdleClips;
    private readonly SomaMotionClip[] _crouchIdleClips;
    private readonly SomaMotionClip? _jumpAirborneClip;
    private readonly SomaMotionClip? _jumpLandingClip;

    /// <summary>
    /// True when the locomotion target tokens (<c>Idle</c>, <c>Walk</c>, <c>Jog</c>, <c>Run</c>)
    /// were loaded from real SOMA BVH mocap clips so inference can condition <c>has_local_poses = 1</c>.
    /// </summary>
    public bool HasMocapTargets { get; }

    public SomaLocomotionPoseBank(
        SomaRestSkeletonCalibration? calibration = null,
        SomaMocapLocomotionAssetSet? mocapAssets = null)
    {
        _calibration = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;
        _runtimeSampler = new HumanoidToSomaPoseSampler(_calibration);
        BuildDefaultTemplates(_calibration);
        _walkTemplate.AsSpan().CopyTo(_strafeForwardTemplate);

        if (mocapAssets is not null)
        {
            CopyIfValid(mocapAssets.IdleTarget, _idleTemplate);
            CopyIfValid(mocapAssets.WalkTarget, _walkTemplate);
            CopyIfValid(mocapAssets.JogTarget, _jogTemplate);
            CopyIfValid(mocapAssets.RunTarget, _runTemplate);
            CopyIfValid(mocapAssets.StrafeForwardTarget ?? mocapAssets.WalkTarget, _strafeForwardTemplate);
            CopyIfValid(mocapAssets.StrafeBackwardTarget, _backwardTemplate);
            CopyIfValid(mocapAssets.StrafeLeftTarget, _strafeLeftTemplate);
            CopyIfValid(mocapAssets.StrafeRightTarget, _strafeRightTemplate);
            CopyIfValid(mocapAssets.CrouchIdleTarget, _crouchIdleTemplate);
            CopyIfValid(mocapAssets.CrouchForwardTarget, _crouchWalkTemplate);
            CopyIfValid(mocapAssets.CrouchBackwardTarget, _crouchBackwardTemplate);
            CopyIfValid(mocapAssets.CrouchLeftTarget, _crouchStrafeLeftTemplate);
            CopyIfValid(mocapAssets.CrouchRightTarget, _crouchStrafeRightTemplate);

            _standingIdleClips = mocapAssets.StandingIdleClips;
            _crouchIdleClips = mocapAssets.CrouchIdleClips;
            _jumpAirborneClip = mocapAssets.JumpAirborneClip;
            _jumpLandingClip = mocapAssets.JumpLandingClip;
            HasMocapTargets = mocapAssets.HasLinearMocapTargets;
        }
        else
        {
            _standingIdleClips = [];
            _crouchIdleClips = [];
            _jumpAirborneClip = null;
            _jumpLandingClip = null;
            HasMocapTargets = false;
        }
    }

    /// <summary>
    /// Returns true when real mocap idle clips are loaded for <paramref name="styleIndex"/>.
    /// </summary>
    public bool HasIdleClips(int styleIndex)
        => styleIndex == CrouchedStyleIndex
            ? _crouchIdleClips.Length > 0
            : styleIndex == StandingStyleIndex && _standingIdleClips.Length > 0;

    /// <summary>
    /// Selects a real mocap idle clip for <paramref name="styleIndex"/>, preferring a different
    /// clip from <paramref name="previousClip"/> when multiple idle variations are loaded.
    /// </summary>
    public SomaMotionClip? SelectIdleClip(int styleIndex, SomaMotionClip? previousClip, ref uint rngState)
    {
        SomaMotionClip[] pool = styleIndex == CrouchedStyleIndex
            ? _crouchIdleClips
            : styleIndex == StandingStyleIndex
                ? _standingIdleClips
                : [];
        if (pool.Length == 0)
            return null;
        if (pool.Length == 1 || previousClip is null)
            return pool[0];

        uint x = rngState == 0u ? 0x2468ACE1u : rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        rngState = x == 0u ? 0x2468ACE1u : x;

        int index = (int)(rngState % (uint)pool.Length);
        if (ReferenceEquals(pool[index], previousClip))
            index = (index + 1) % pool.Length;
        return pool[index];
    }

    private static void CopyIfValid(SomaPoseTransform[]? source, SomaPoseTransform[] destination)
    {
        if (source is not null && source.Length >= TemplateSize)
            source.AsSpan(0, TemplateSize).CopyTo(destination);
    }

    /// <summary>
    /// Copies frame 0 of the upright relaxed idle template into <paramref name="destination"/>.
    /// </summary>
    public void CopyIdlePose(Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        _idleTemplate.AsSpan(0, JointCount).CopyTo(destination);
    }

    /// <summary>
    /// Copies frame 0 of the upright relaxed idle template rotated to face
    /// <paramref name="facingYawRadians"/> (where 0 faces +Z) into <paramref name="destination"/>.
    /// </summary>
    public void CopyIdlePoseAtYaw(float facingYawRadians, Span<SomaPoseTransform> destination)
    {
        CopyIdlePose(destination);
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        destination[0] = new SomaPoseTransform(
            destination[0].Position,
            SomaPoseTransform.NormalizeOrIdentity(facingRot * destination[0].Rotation));
    }

    /// <summary>
    /// Copies frame 0 of the crouched idle template placed at <paramref name="rootGroundXZ"/>
    /// and facing <paramref name="facingYawRadians"/> into <paramref name="destination"/>.
    /// </summary>
    private void CopyCrouchIdlePoseAtYaw(
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        _crouchIdleTemplate.AsSpan(0, JointCount).CopyTo(destination);
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        destination[0] = new SomaPoseTransform(
            new Vector3(rootGroundXZ.X, destination[0].Position.Y, rootGroundXZ.Z),
            SomaPoseTransform.NormalizeOrIdentity(facingRot * destination[0].Rotation));
    }

    /// <summary>
    /// Builds a velocity-blended airborne jump or fall pose placed at <paramref name="rootGroundXZ"/>
    /// and facing <paramref name="facingYawRadians"/>. Uses the staged NVIDIA SOMA jump BVH clip
    /// when available, and uses procedural jump ascent and fall poses otherwise.
    /// </summary>
    public void BuildAirbornePose(
        float verticalVelocity,
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        if (_jumpAirborneClip is not null)
        {
            // Map vertical velocity (+4.6 m/s launch -> 0.0 m/s apex -> -4.6 m/s descent)
            // monotonically across the SOMA jump clip's airborne arc.
            float flightPhase = Math.Clamp((4.6f - verticalVelocity) / 9.2f, 0.0f, 1.0f);
            _jumpAirborneClip.SamplePlacedFrame(
                flightPhase * _jumpAirborneClip.DurationSeconds,
                loop: false,
                rootGroundXZ,
                facingYawRadians,
                destination);
            return;
        }

        // +3.0 m/s -> pure jump ascent; <= -1.5 m/s -> pure fall anticipation.
        float fallWeight = Math.Clamp((2.2f - verticalVelocity) / 3.7f, 0.0f, 1.0f);
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);

        SomaPoseTransform rootPose = SomaPoseTransform.Lerp(_jumpAscentPose[0], _jumpFallPose[0], fallWeight);
        destination[0] = new SomaPoseTransform(
            new Vector3(rootGroundXZ.X, rootPose.Position.Y, rootGroundXZ.Z),
            SomaPoseTransform.NormalizeOrIdentity(facingRot * rootPose.Rotation));

        for (int joint = 1; joint < JointCount; joint++)
            destination[joint] = SomaPoseTransform.Lerp(_jumpAscentPose[joint], _jumpFallPose[joint], fallWeight);
    }

    /// <summary>
    /// Builds a landing-absorption pose for a moving landing (walk, jog, or run) by placing
    /// the matched-speed stride template at <paramref name="rootGroundXZ"/> and adding a brief
    /// athletic knee-absorption dip so forward momentum continues without resetting to idle.
    /// </summary>
    public void BuildMovingLandingPose(
        float normalizedSpeed,
        float landingProgress01,
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination,
        int styleIndex = StandingStyleIndex)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        BuildLinear1DTarget(
            normalizedSpeed,
            targetSpeedMetersPerSecond: 0.0f,
            rootGroundXZ,
            facingYawRadians,
            _scratchB,
            styleIndex);

        _scratchB.AsSpan(0, JointCount).CopyTo(destination);

        float progress = Math.Clamp(landingProgress01, 0.0f, 1.0f);
        // Quick touchdown dip that peaks near impact (progress ~ 0.35) and springs smoothly back up.
        float impactDip = MathF.Sin(MathF.PI * progress);
        float dipMeters = styleIndex == CrouchedStyleIndex ? 0.045f * impactDip : 0.075f * impactDip;
        SomaPoseTransform root = destination[0];
        destination[0] = new SomaPoseTransform(
            new Vector3(rootGroundXZ.X, MathF.Max(0.42f, root.Position.Y - dipMeters), rootGroundXZ.Z),
            root.Rotation);
    }

    /// <summary>
    /// Synthesizes 4 velocity-consistent 30 Hz stride context poses ending at
    /// <paramref name="currentRootXZ"/> so a post-landing SOMA replan continues directly at
    /// walking or running speed instead of restarting from an airborne or idle context.
    /// </summary>
    public void BuildMovingLandingContextFrames(
        float normalizedSpeed,
        Vector2 somaPlanarVelocityXZ,
        Vector3 currentRootXZ,
        float facingYawRadians,
        int styleIndex,
        Span<SomaPoseTransform> destinationFourFrames)
    {
        if (destinationFourFrames.Length < TemplateSize)
            throw new ArgumentException($"Destination span must hold {TemplateSize} transforms.", nameof(destinationFourFrames));

        float speed = somaPlanarVelocityXZ.Length();
        BuildLinear1DTarget(
            normalizedSpeed,
            targetSpeedMetersPerSecond: speed,
            currentRootXZ,
            facingYawRadians,
            destinationFourFrames,
            styleIndex);

        // Anchor the newest context frame (frame 3) at currentRootXZ and step earlier frames
        // backward along somaPlanarVelocityXZ at 30 Hz so SOMA sees exact current root velocity.
        const float dt30 = 1.0f / SomaModelContract.SampleRateHz;
        for (int frame = 0; frame < TemplateFrames; frame++)
        {
            int rootIdx = frame * JointCount;
            SomaPoseTransform root = destinationFourFrames[rootIdx];
            float backSteps = (TemplateFrames - 1) - frame;
            destinationFourFrames[rootIdx] = new SomaPoseTransform(
                new Vector3(
                    currentRootXZ.X - somaPlanarVelocityXZ.X * (backSteps * dt30),
                    root.Position.Y,
                    currentRootXZ.Z - somaPlanarVelocityXZ.Y * (backSteps * dt30)),
                root.Rotation);
        }
    }

    /// <summary>
    /// Builds a landing-absorption pose at normalized <paramref name="landingProgress01"/>
    /// (<c>0</c> = touchdown impact, <c>1</c> = stride recovery) placed at
    /// <paramref name="rootGroundXZ"/> and facing <paramref name="facingYawRadians"/>.
    /// </summary>
    public void BuildLandingPose(
        float landingProgress01,
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        if (_jumpLandingClip is not null)
        {
            float progress = Math.Clamp(landingProgress01, 0.0f, 1.0f);
            _jumpLandingClip.SamplePlacedFrame(
                progress * _jumpLandingClip.DurationSeconds,
                loop: false,
                rootGroundXZ,
                facingYawRadians,
                destination);
            return;
        }

        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        destination[0] = new SomaPoseTransform(
            new Vector3(rootGroundXZ.X, _landingPose[0].Position.Y, rootGroundXZ.Z),
            SomaPoseTransform.NormalizeOrIdentity(facingRot * _landingPose[0].Rotation));

        for (int joint = 1; joint < JointCount; joint++)
            destination[joint] = _landingPose[joint];
    }

    /// <summary>
    /// Builds a calm standing or crouched idle pose with gentle breathing and posture sway,
    /// anchored at <paramref name="rootGroundXZ"/> and facing <paramref name="facingYawRadians"/>
    /// without heap allocations. Scales cyclic motion by <paramref name="breathingAmplitudeScale"/>
    /// so cross-fades start at the exact neutral idle template and ease breathing in smoothly.
    /// </summary>
    public void BuildBreathingIdlePose(
        float idleTimeSeconds,
        int styleIndex,
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination,
        float breathingAmplitudeScale = 1.0f)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        bool mecanim = _calibration.UseMecanimNeutralPostureOffsets;
        float t = float.IsFinite(idleTimeSeconds) ? idleTimeSeconds : 0.0f;
        float amplitude = float.IsFinite(breathingAmplitudeScale)
            ? Math.Clamp(breathingAmplitudeScale, 0.0f, 1.0f)
            : 1.0f;

        // ~13 breaths/min chest cycle and slow organic weight-sway cycle.
        float breath = MathF.Sin(t * (2.0f * MathF.PI * 0.22f)) * amplitude;
        float sway = MathF.Sin(t * (2.0f * MathF.PI * 0.09f)) * amplitude;
        float headDrift = MathF.Sin(t * (2.0f * MathF.PI * 0.06f) + 0.8f) * amplitude;

        Array.Clear(_runtimeMuscleScratch);

        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        Vector3 localSwayOffset;
        float hipsY;

        if (styleIndex == CrouchedStyleIndex)
        {
            // Physically balanced staggered athletic crouch with gentle breathing.
            _runtimeMuscleScratch[(int)EHumanoidValue.SpineFrontBack] = -0.24f + breath * 0.014f;
            _runtimeMuscleScratch[(int)EHumanoidValue.ChestFrontBack] = -0.14f + breath * 0.022f;
            _runtimeMuscleScratch[(int)EHumanoidValue.UpperChestFrontBack] = breath * 0.018f;
            _runtimeMuscleScratch[(int)EHumanoidValue.SpineLeftRight] = sway * 0.012f;
            _runtimeMuscleScratch[(int)EHumanoidValue.NeckNodDownUp] = 0.18f - breath * 0.010f;
            _runtimeMuscleScratch[(int)EHumanoidValue.HeadNodDownUp] = 0.16f;
            _runtimeMuscleScratch[(int)EHumanoidValue.HeadTurnLeftRight] = headDrift * 0.025f;

            float armDownUp = (mecanim ? -0.82f : -0.90f) + breath * 0.012f;
            float forearmStretch = (mecanim ? 0.62f : 0.25f) - breath * 0.012f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftShoulderDownUp] = -0.10f + breath * 0.015f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightShoulderDownUp] = -0.10f + breath * 0.015f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmDownUp] = armDownUp;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightArmDownUp] = armDownUp;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmFrontBack] = -0.18f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightArmFrontBack] = -0.14f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftForearmStretch] = forearmStretch;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightForearmStretch] = forearmStretch;

            _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegFrontBack] = mecanim ? -0.28f : -0.78f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegFrontBack] = mecanim ? 0.06f : -0.45f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegInOut] = 0.15f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegInOut] = 0.15f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftLowerLegStretch] = mecanim ? -0.16f : -0.92f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightLowerLegStretch] = mecanim ? -0.34f : -0.95f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftFootUpDown] = -0.52f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightFootUpDown] = -0.22f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightToesUpDown] = -0.30f;

            hipsY = MathF.Max(0.42f, _calibration.HipsHeightMeters - 0.36f + breath * 0.005f);
            localSwayOffset = new Vector3(sway * 0.005f, 0.0f, 0.0f);
        }
        else
        {
            // Poised, feminine standing idle with subtle contrapposto weight shift, level forward
            // head pitch, narrow stance, soft elbows, relaxed wrists, flat feet, and organic breathing.
            _runtimeMuscleScratch[(int)EHumanoidValue.SpineFrontBack] = 0.02f + breath * 0.012f;
            _runtimeMuscleScratch[(int)EHumanoidValue.ChestFrontBack] = 0.03f + breath * 0.020f;
            _runtimeMuscleScratch[(int)EHumanoidValue.UpperChestFrontBack] = 0.01f + breath * 0.014f;
            _runtimeMuscleScratch[(int)EHumanoidValue.SpineLeftRight] = 0.035f + sway * 0.014f;
            _runtimeMuscleScratch[(int)EHumanoidValue.ChestLeftRight] = -0.028f - sway * 0.010f;
            _runtimeMuscleScratch[(int)EHumanoidValue.NeckNodDownUp] = -0.09f - breath * 0.010f;
            _runtimeMuscleScratch[(int)EHumanoidValue.HeadNodDownUp] = -0.14f;
            _runtimeMuscleScratch[(int)EHumanoidValue.HeadTurnLeftRight] = headDrift * 0.026f;
            _runtimeMuscleScratch[(int)EHumanoidValue.HeadTiltLeftRight] = 0.018f + sway * 0.012f;

            float armDownUp = (mecanim ? -0.78f : -0.89f) + breath * 0.012f;
            float forearmStretch = (mecanim ? 0.56f : 0.16f) - breath * 0.012f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftShoulderDownUp] = -0.09f + breath * 0.014f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightShoulderDownUp] = -0.09f + breath * 0.014f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmDownUp] = armDownUp;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightArmDownUp] = armDownUp;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmFrontBack] = -0.06f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightArmFrontBack] = -0.04f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftForearmStretch] = forearmStretch;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightForearmStretch] = forearmStretch;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftHandDownUp] = -0.14f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightHandDownUp] = -0.14f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftHandInOut] = -0.06f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightHandInOut] = -0.06f;

            _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegFrontBack] = mecanim ? 0.54f : -0.02f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegFrontBack] = mecanim ? 0.58f : 0.02f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegInOut] = 0.02f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegInOut] = 0.04f;
            _runtimeMuscleScratch[(int)EHumanoidValue.LeftLowerLegStretch] = mecanim ? 0.86f : -0.06f;
            _runtimeMuscleScratch[(int)EHumanoidValue.RightLowerLegStretch] = mecanim ? 0.90f : -0.04f;

            hipsY = _calibration.HipsHeightMeters - 0.02f + breath * 0.004f;
            localSwayOffset = new Vector3(sway * 0.006f, 0.0f, 0.0f);
        }

        Vector3 worldSway = Vector3.Transform(localSwayOffset, facingRot);
        var bodyPos = new Vector3(rootGroundXZ.X + worldSway.X, hipsY, rootGroundXZ.Z + worldSway.Z);
        _runtimeSampler.SampleSomaPose(
            bodyPos,
            facingRot,
            _runtimeMuscleScratch,
            avatarLegScale: 1.0f,
            destination);
    }

    /// <summary>
    /// Blends the 1D linear locomotion templates (<c>Idle -> Walk -> Jog -> Run</c>, or crouch and
    /// prone equivalents selected by <paramref name="styleIndex"/>) for a normalized speed parameter
    /// in <c>[0, 1]</c> and places the resulting target poses at <paramref name="targetGroundPosition"/>
    /// facing <paramref name="facingYawRadians"/>.
    /// </summary>
    public void BuildLinear1DTarget(
        float normalizedSpeed,
        float targetSpeedMetersPerSecond,
        Vector3 targetGroundPosition,
        float facingYawRadians,
        Span<SomaPoseTransform> destination,
        int styleIndex = StandingStyleIndex)
    {
        float t = Math.Clamp(normalizedSpeed, 0.0f, 1.0f);
        if (styleIndex == CrouchedStyleIndex)
        {
            BlendTemplates(_crouchIdleTemplate, _crouchWalkTemplate, Math.Clamp(t / 0.45f, 0.0f, 1.0f), _scratchA);
        }
        else if (styleIndex == ProneStyleIndex)
        {
            BlendTemplates(_proneIdleTemplate, _proneMoveTemplate, Math.Clamp(t / 0.35f, 0.0f, 1.0f), _scratchA);
        }
        else if (t <= 0.45f)
        {
            BlendTemplates(_idleTemplate, _walkTemplate, t / 0.45f, _scratchA);
        }
        else if (t <= 0.72f)
        {
            BlendTemplates(_walkTemplate, _jogTemplate, (t - 0.45f) / 0.27f, _scratchA);
        }
        else
        {
            BlendTemplates(_jogTemplate, _runTemplate, (t - 0.72f) / 0.28f, _scratchA);
        }

        PlaceAndScaleVelocity(_scratchA, targetGroundPosition, facingYawRadians, targetSpeedMetersPerSecond, 0.0f, destination);
    }

    /// <summary>
    /// Blends the 2D directional strafe templates (<c>Idle, Forward, Backward, Left, Right</c>,
    /// or crouch and prone equivalents selected by <paramref name="styleIndex"/>) using local-space
    /// velocity components <paramref name="localForward"/> and <paramref name="localRight"/>
    /// (both in <c>[-1, 1]</c>) while facing <paramref name="facingYawRadians"/>.
    /// </summary>
    public void BuildDirectional2DTarget(
        float localForward,
        float localRight,
        float targetSpeedMetersPerSecond,
        Vector3 targetGroundPosition,
        float facingYawRadians,
        float travelYawRadians,
        Span<SomaPoseTransform> destination,
        int styleIndex = StandingStyleIndex)
    {
        SomaPoseTransform[] idleTemplate = styleIndex switch
        {
            CrouchedStyleIndex => _crouchIdleTemplate,
            ProneStyleIndex => _proneIdleTemplate,
            _ => _idleTemplate,
        };

        float mag = Math.Clamp(MathF.Sqrt(localForward * localForward + localRight * localRight), 0.0f, 1.0f);
        if (mag <= 1.0e-4f)
        {
            PlaceAndScaleVelocity(idleTemplate, targetGroundPosition, facingYawRadians, 0.0f, 0.0f, destination);
            return;
        }

        SomaPoseTransform[] fbTemplate;
        SomaPoseTransform[] lrTemplate;
        if (styleIndex == CrouchedStyleIndex)
        {
            fbTemplate = localForward >= 0.0f ? _crouchWalkTemplate : _crouchBackwardTemplate;
            lrTemplate = localRight >= 0.0f ? _crouchStrafeRightTemplate : _crouchStrafeLeftTemplate;
        }
        else if (styleIndex == ProneStyleIndex)
        {
            fbTemplate = _proneMoveTemplate;
            lrTemplate = _proneMoveTemplate;
        }
        else
        {
            fbTemplate = localForward >= 0.0f ? _strafeForwardTemplate : _backwardTemplate;
            lrTemplate = localRight >= 0.0f ? _strafeRightTemplate : _strafeLeftTemplate;
        }

        float fbAbs = MathF.Abs(localForward);
        float lrAbs = MathF.Abs(localRight);
        float axisSum = fbAbs + lrAbs;
        float lrWeight = axisSum > 1.0e-5f ? lrAbs / axisSum : 0.0f;

        BlendTemplates(fbTemplate, lrTemplate, lrWeight, _scratchB);
        BlendTemplates(idleTemplate, _scratchB, mag, _scratchA);

        float relativeTravelYaw = CriticalSpringDamper.WrapAngleRadians(travelYawRadians - facingYawRadians);
        PlaceAndScaleVelocity(_scratchA, targetGroundPosition, facingYawRadians, targetSpeedMetersPerSecond, relativeTravelYaw, destination);
    }
}
