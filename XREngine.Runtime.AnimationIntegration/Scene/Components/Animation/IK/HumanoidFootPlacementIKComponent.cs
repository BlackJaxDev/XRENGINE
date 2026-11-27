using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Post-animation world-space foot placement and terrain IK component that runs after
/// <see cref="AnimStateMachineComponent"/> applies layered muscles. Drops the pelvis on slopes
/// and stairs (clamped to leg reach), aligns ankle rotations to surface normals, locks planted
/// stance feet against horizontal sliding, and solves two-bone leg IK with zero per-frame allocations.
/// </summary>
[Category("Animation")]
[DisplayName("Humanoid Foot Placement IK")]
[Description("Plants animated feet onto slopes and stairs using physics raycasts, pelvis drop, ankle tilt, and stance foot locking.")]
public sealed class HumanoidFootPlacementIKComponent : XRComponent
{
    private static readonly PhysicsQueryFilter StaticGroundFilter = new(
        PhysicsQueryActorTypes.Static,
        PhysicsQueryHitDetail.Position | PhysicsQueryHitDetail.Normal);

    private readonly SortedDictionary<float, List<(XRComponent? item, object? data)>> _sweepScratch = [];

    private HumanoidComponent? _humanoid;
    private float _weight = 1.0f;
    private float _footSoleHeightOffset = 0.08f;
    private float _probeUpMeters = 0.45f;
    private float _probeDownMeters = 0.90f;
    private float _probeRadiusMeters = 0.07f;
    private float _maxPelvisDropMeters = 0.45f;
    private float _maxAnkleTiltDegrees = 42.0f;
    private float _pelvisSmoothingSpeed = 16.0f;
    private bool _enableFootLocking = true;
    private float _footLockHeightThresholdMeters = 0.055f;
    private float _footLockSpeedThresholdMetersPerSecond = 0.35f;
    private float _footUnlockDistanceMeters = 0.22f;

    private float _smoothedPelvisDrop;
    private bool _pelvisDropInitialized;

    private bool _leftFootLocked;
    private bool _rightFootLocked;
    private Vector3 _leftLockWorldPos;
    private Vector3 _rightLockWorldPos;
    private Vector3 _prevRawLeftFootPos;
    private Vector3 _prevRawRightFootPos;
    private bool _prevFootPositionsValid;

    public HumanoidComponent? Humanoid
    {
        get => _humanoid;
        set => SetField(ref _humanoid, value);
    }

    /// <summary>Optional custom ground prober (used by tests or non-physics terrains).</summary>
    public IFootGroundProber? GroundProberOverride { get; set; }

    public float Weight
    {
        get => _weight;
        set => SetField(ref _weight, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 1.0f);
    }

    public float FootSoleHeightOffset
    {
        get => _footSoleHeightOffset;
        set => SetField(ref _footSoleHeightOffset, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.25f) : 0.08f);
    }

    public float ProbeUpMeters
    {
        get => _probeUpMeters;
        set => SetField(ref _probeUpMeters, float.IsFinite(value) ? Math.Clamp(value, 0.10f, 1.5f) : 0.45f);
    }

    public float ProbeDownMeters
    {
        get => _probeDownMeters;
        set => SetField(ref _probeDownMeters, float.IsFinite(value) ? Math.Clamp(value, 0.15f, 2.5f) : 0.90f);
    }

    public float MaxPelvisDropMeters
    {
        get => _maxPelvisDropMeters;
        set => SetField(ref _maxPelvisDropMeters, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.85f) : 0.45f);
    }

    public float MaxAnkleTiltDegrees
    {
        get => _maxAnkleTiltDegrees;
        set => SetField(ref _maxAnkleTiltDegrees, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 65.0f) : 42.0f);
    }

    public float PelvisSmoothingSpeed
    {
        get => _pelvisSmoothingSpeed;
        set => SetField(ref _pelvisSmoothingSpeed, float.IsFinite(value) ? MathF.Max(0.5f, value) : 16.0f);
    }

    public bool EnableFootLocking
    {
        get => _enableFootLocking;
        set => SetField(ref _enableFootLocking, value);
    }

    public float FootLockHeightThresholdMeters
    {
        get => _footLockHeightThresholdMeters;
        set => SetField(ref _footLockHeightThresholdMeters, float.IsFinite(value) ? Math.Clamp(value, 0.005f, 0.25f) : 0.055f);
    }

    public float FootLockSpeedThresholdMetersPerSecond
    {
        get => _footLockSpeedThresholdMetersPerSecond;
        set => SetField(ref _footLockSpeedThresholdMetersPerSecond, float.IsFinite(value) ? Math.Clamp(value, 0.05f, 2.5f) : 0.35f);
    }

    public float FootUnlockDistanceMeters
    {
        get => _footUnlockDistanceMeters;
        set => SetField(ref _footUnlockDistanceMeters, float.IsFinite(value) ? Math.Clamp(value, 0.05f, 0.80f) : 0.22f);
    }

    public float LastAppliedPelvisDropMeters { get; private set; }
    public bool LeftFootLocked => _leftFootLocked;
    public bool RightFootLocked => _rightFootLocked;
    public Vector3 LastLeftFootTarget { get; private set; }
    public Vector3 LastRightFootTarget { get; private set; }
    public Vector3 LastLeftGroundNormal { get; private set; } = Vector3.UnitY;
    public Vector3 LastRightGroundNormal { get; private set; } = Vector3.UnitY;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        ResetState();
        RegisterTick(ETickGroup.Normal, (int)ETickOrder.Animation + 1, TickFootPlacement);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Animation + 1, TickFootPlacement);
        ResetState();
        base.OnComponentDeactivated();
    }

    public void ResetState()
    {
        _smoothedPelvisDrop = 0.0f;
        _pelvisDropInitialized = false;
        _leftFootLocked = false;
        _rightFootLocked = false;
        _prevFootPositionsValid = false;
        LastAppliedPelvisDropMeters = 0.0f;
    }

    private void TickFootPlacement()
    {
        float dt = RuntimeTransformServices.Current?.DilatedUpdateDeltaSeconds ?? 0.0f;
        StepFootPlacement(dt);
    }

    /// <summary>
    /// Evaluates terrain probes, pelvis drop, stance foot locking, two-bone leg IK, and
    /// ankle normal alignment for the current animated humanoid pose.
    /// </summary>
    public void StepFootPlacement(float deltaSeconds)
    {
        if (_weight <= 1.0e-4f)
            return;

        HumanoidComponent? humanoid = _humanoid
            ?? (SceneNode?.GetComponentInHierarchy(nameof(HumanoidComponent)) as HumanoidComponent);
        if (humanoid is null)
            return;

        Transform? hips = humanoid.Hips.Node?.Transform as Transform;
        Transform? leftUpperLeg = humanoid.Left.Leg.Node?.Transform as Transform;
        Transform? leftLowerLeg = humanoid.Left.Knee.Node?.Transform as Transform;
        Transform? leftFoot = humanoid.Left.Foot.Node?.Transform as Transform;
        Transform? rightUpperLeg = humanoid.Right.Leg.Node?.Transform as Transform;
        Transform? rightLowerLeg = humanoid.Right.Knee.Node?.Transform as Transform;
        Transform? rightFoot = humanoid.Right.Foot.Node?.Transform as Transform;

        if (hips is null || leftUpperLeg is null || leftLowerLeg is null || leftFoot is null
            || rightUpperLeg is null || rightLowerLeg is null || rightFoot is null)
        {
            return;
        }

        hips.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);

        Vector3 rawLeftPos = leftFoot.WorldTranslation;
        Vector3 rawRightPos = rightFoot.WorldTranslation;

        bool leftHit = TryProbeFootGround(rawLeftPos, out Vector3 leftGround, out Vector3 leftNormal);
        bool rightHit = TryProbeFootGround(rawRightPos, out Vector3 rightGround, out Vector3 rightNormal);
        if (!leftHit && !rightHit)
        {
            _leftFootLocked = false;
            _rightFootLocked = false;
            _prevRawLeftFootPos = rawLeftPos;
            _prevRawRightFootPos = rawRightPos;
            _prevFootPositionsValid = true;
            LastAppliedPelvisDropMeters = 0.0f;
            return;
        }

        // 1. Update stance foot locks before computing leg reach.
        Vector3 targetLeftPos = leftHit
            ? new Vector3(rawLeftPos.X, leftGround.Y + _footSoleHeightOffset, rawLeftPos.Z)
            : rawLeftPos;
        Vector3 targetRightPos = rightHit
            ? new Vector3(rawRightPos.X, rightGround.Y + _footSoleHeightOffset, rawRightPos.Z)
            : rawRightPos;

        if (_enableFootLocking)
        {
            UpdateFootLock(
                rawLeftPos,
                leftHit,
                leftGround.Y + _footSoleHeightOffset,
                _prevFootPositionsValid ? _prevRawLeftFootPos : rawLeftPos,
                deltaSeconds,
                ref _leftFootLocked,
                ref _leftLockWorldPos,
                ref targetLeftPos);

            UpdateFootLock(
                rawRightPos,
                rightHit,
                rightGround.Y + _footSoleHeightOffset,
                _prevFootPositionsValid ? _prevRawRightFootPos : rawRightPos,
                deltaSeconds,
                ref _rightFootLocked,
                ref _rightLockWorldPos,
                ref targetRightPos);
        }
        else
        {
            _leftFootLocked = false;
            _rightFootLocked = false;
        }

        _prevRawLeftFootPos = rawLeftPos;
        _prevRawRightFootPos = rawRightPos;
        _prevFootPositionsValid = true;

        // 2. Compute required pelvis drop so the lower leg reaches without hyperextension.
        float leftDeltaY = leftHit ? (targetLeftPos.Y - rawLeftPos.Y) : 0.0f;
        float rightDeltaY = rightHit ? (targetRightPos.Y - rawRightPos.Y) : 0.0f;
        float desiredDrop = MathF.Min(0.0f, MathF.Min(leftDeltaY, rightDeltaY));

        float leftReach = EstimateLegLength(leftUpperLeg, leftLowerLeg, leftFoot);
        float rightReach = EstimateLegLength(rightUpperLeg, rightLowerLeg, rightFoot);
        float maxSafeDrop = -MathF.Min(_maxPelvisDropMeters, MathF.Min(leftReach, rightReach) * 0.55f);
        desiredDrop = Math.Clamp(desiredDrop * _weight, maxSafeDrop, 0.0f);

        if (!_pelvisDropInitialized || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f)
        {
            _smoothedPelvisDrop = desiredDrop;
            _pelvisDropInitialized = true;
        }
        else
        {
            float alpha = 1.0f - MathF.Exp(-_pelvisSmoothingSpeed * deltaSeconds);
            _smoothedPelvisDrop += (desiredDrop - _smoothedPelvisDrop) * alpha;
        }

        LastAppliedPelvisDropMeters = -_smoothedPelvisDrop;

        if (MathF.Abs(_smoothedPelvisDrop) > 1.0e-5f)
        {
            Vector3 hipsWorld = hips.WorldTranslation + new Vector3(0.0f, _smoothedPelvisDrop, 0.0f);
            hips.SetWorldTranslationRotation(hipsWorld, hips.WorldRotation);
            hips.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        }

        // 3. Solve analytical two-bone leg IK and align ankles to ground surface normals.
        if (leftHit)
        {
            Vector3 blendedLeft = Vector3.Lerp(leftFoot.WorldTranslation, targetLeftPos, _weight);
            SolveTwoBoneLegIK(leftUpperLeg, leftLowerLeg, leftFoot, blendedLeft);
            AlignAnkleToNormal(leftFoot, leftNormal, _weight, _maxAnkleTiltDegrees);
            LastLeftFootTarget = blendedLeft;
            LastLeftGroundNormal = leftNormal;
        }

        if (rightHit)
        {
            Vector3 blendedRight = Vector3.Lerp(rightFoot.WorldTranslation, targetRightPos, _weight);
            SolveTwoBoneLegIK(rightUpperLeg, rightLowerLeg, rightFoot, blendedRight);
            AlignAnkleToNormal(rightFoot, rightNormal, _weight, _maxAnkleTiltDegrees);
            LastRightFootTarget = blendedRight;
            LastRightGroundNormal = rightNormal;
        }

        hips.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
    }

    private void UpdateFootLock(
        Vector3 rawFootPos,
        bool hasGroundHit,
        float groundAnkleY,
        Vector3 prevRawFootPos,
        float dt,
        ref bool locked,
        ref Vector3 lockedPos,
        ref Vector3 targetPos)
    {
        if (!hasGroundHit)
        {
            locked = false;
            return;
        }

        float heightAboveGround = rawFootPos.Y - groundAnkleY;
        float horizSpeed = dt > 1.0e-4f
            ? MathF.Sqrt((rawFootPos.X - prevRawFootPos.X) * (rawFootPos.X - prevRawFootPos.X)
                + (rawFootPos.Z - prevRawFootPos.Z) * (rawFootPos.Z - prevRawFootPos.Z)) / dt
            : 0.0f;

        if (!locked)
        {
            if (heightAboveGround <= _footLockHeightThresholdMeters
                && horizSpeed <= _footLockSpeedThresholdMetersPerSecond)
            {
                locked = true;
                lockedPos = new Vector3(rawFootPos.X, groundAnkleY, rawFootPos.Z);
            }
        }
        else
        {
            float driftXZ = MathF.Sqrt(
                (rawFootPos.X - lockedPos.X) * (rawFootPos.X - lockedPos.X)
                + (rawFootPos.Z - lockedPos.Z) * (rawFootPos.Z - lockedPos.Z));
            if (heightAboveGround > _footLockHeightThresholdMeters * 1.4f
                || horizSpeed > _footLockSpeedThresholdMetersPerSecond * 1.6f
                || driftXZ > _footUnlockDistanceMeters)
            {
                locked = false;
            }
            else
            {
                lockedPos.Y = groundAnkleY;
                targetPos = lockedPos;
            }
        }
    }

    private bool TryProbeFootGround(
        Vector3 footWorldPos,
        out Vector3 groundPoint,
        out Vector3 groundNormal)
    {
        if (GroundProberOverride is not null)
            return GroundProberOverride.TryProbeGround(footWorldPos, _probeUpMeters, _probeDownMeters, out groundPoint, out groundNormal);

        groundPoint = footWorldPos;
        groundNormal = Vector3.UnitY;

        if (World is not IRuntimePhysicsWorldContext physicsWorld)
            return false;

        Vector3 start = new(footWorldPos.X, footWorldPos.Y + _probeUpMeters, footWorldPos.Z);
        float maxDist = _probeUpMeters + _probeDownMeters;
        _sweepScratch.Clear();

        bool hit = physicsWorld.PhysicsScene.SweepSingle(
            new IPhysicsGeometry.Sphere(_probeRadiusMeters),
            (start, Quaternion.Identity),
            -Vector3.UnitY,
            maxDist,
            LayerMask.Everything,
            StaticGroundFilter,
            _sweepScratch);
        if (!hit || _sweepScratch.Count == 0)
            return false;

        foreach ((float dist, List<(XRComponent? item, object? data)> hits) in _sweepScratch)
        {
            if (!float.IsFinite(dist) || hits.Count == 0)
                continue;

            float surfaceY = start.Y - dist - _probeRadiusMeters;
            groundPoint = new Vector3(footWorldPos.X, surfaceY, footWorldPos.Z);
            if (hits[0].data is SweepHit sweepHit && sweepHit.Normal.LengthSquared() > 1.0e-6f)
                groundNormal = Vector3.Normalize(sweepHit.Normal);

            return true;
        }

        return false;
    }

    private static float EstimateLegLength(Transform upperLeg, Transform lowerLeg, Transform foot)
    {
        float upperLen = Vector3.Distance(upperLeg.WorldTranslation, lowerLeg.WorldTranslation);
        float lowerLen = Vector3.Distance(lowerLeg.WorldTranslation, foot.WorldTranslation);
        return MathF.Max(0.2f, upperLen + lowerLen);
    }

    private static void SolveTwoBoneLegIK(
        Transform upperLeg,
        Transform lowerLeg,
        Transform foot,
        Vector3 targetFootWorld)
    {
        Vector3 hipPos = upperLeg.WorldTranslation;
        Vector3 kneePos = lowerLeg.WorldTranslation;
        Vector3 footPos = foot.WorldTranslation;

        Vector3 upperVec = kneePos - hipPos;
        Vector3 lowerVec = footPos - kneePos;
        float upperLen = upperVec.Length();
        float lowerLen = lowerVec.Length();
        if (upperLen < 1.0e-4f || lowerLen < 1.0e-4f)
            return;

        Vector3 toTarget = targetFootWorld - hipPos;
        float targetDist = toTarget.Length();
        float minDist = MathF.Abs(upperLen - lowerLen) + 1.0e-3f;
        float maxDist = (upperLen + lowerLen) * 0.998f;
        float clampedDist = Math.Clamp(targetDist, minDist, maxDist);
        if (targetDist < 1.0e-5f)
            return;

        Vector3 toTargetDir = toTarget / targetDist;
        Vector3 currentDir = footPos - hipPos;
        float currentDist = currentDir.Length();
        if (currentDist < 1.0e-5f)
            return;
        currentDir /= currentDist;

        // Determine a stable knee bend axis perpendicular to the leg plane.
        Vector3 bendAxis = Vector3.Cross(upperVec, lowerVec);
        if (bendAxis.LengthSquared() < 1.0e-6f)
            bendAxis = Vector3.Cross(toTargetDir, Vector3.UnitZ);
        if (bendAxis.LengthSquared() < 1.0e-6f)
            bendAxis = Vector3.UnitX;
        bendAxis = Vector3.Normalize(bendAxis);

        // Compute desired interior angles using law of cosines.
        float cosCurHip = Math.Clamp(
            (upperLen * upperLen + currentDist * currentDist - lowerLen * lowerLen) / (2.0f * upperLen * currentDist),
            -1.0f,
            1.0f);
        float cosNewHip = Math.Clamp(
            (upperLen * upperLen + clampedDist * clampedDist - lowerLen * lowerLen) / (2.0f * upperLen * clampedDist),
            -1.0f,
            1.0f);
        float hipAngleDelta = MathF.Acos(cosNewHip) - MathF.Acos(cosCurHip);

        float cosCurKnee = Math.Clamp(
            (upperLen * upperLen + lowerLen * lowerLen - currentDist * currentDist) / (2.0f * upperLen * lowerLen),
            -1.0f,
            1.0f);
        float cosNewKnee = Math.Clamp(
            (upperLen * upperLen + lowerLen * lowerLen - clampedDist * clampedDist) / (2.0f * upperLen * lowerLen),
            -1.0f,
            1.0f);
        float kneeAngleDelta = MathF.Acos(cosCurKnee) - MathF.Acos(cosNewKnee);

        // 1. Align overall hip-to-foot direction onto hip-to-target and apply hip bend delta.
        Quaternion aimDelta = CreateShortestArcRotation(currentDir, toTargetDir);
        Quaternion hipBendRot = Quaternion.CreateFromAxisAngle(bendAxis, -hipAngleDelta);
        Quaternion upperWorldRot = Quaternion.Normalize(aimDelta * hipBendRot * upperLeg.WorldRotation);
        upperLeg.SetWorldTranslationRotation(upperLeg.WorldTranslation, upperWorldRot);
        RecalculateChain(upperLeg, lowerLeg, foot);

        // 2. Apply knee bend delta so the ankle lands on targetFootWorld.
        Vector3 newUpperDir = Vector3.Normalize(lowerLeg.WorldTranslation - upperLeg.WorldTranslation);
        Vector3 newLowerDir = Vector3.Normalize(foot.WorldTranslation - lowerLeg.WorldTranslation);
        Vector3 newBendAxis = Vector3.Cross(newUpperDir, newLowerDir);
        if (newBendAxis.LengthSquared() < 1.0e-6f)
            newBendAxis = Vector3.Transform(bendAxis, aimDelta);
        newBendAxis = Vector3.Normalize(newBendAxis);

        Quaternion kneeRot = Quaternion.CreateFromAxisAngle(newBendAxis, kneeAngleDelta);
        lowerLeg.SetWorldTranslationRotation(lowerLeg.WorldTranslation, Quaternion.Normalize(kneeRot * lowerLeg.WorldRotation));
        RecalculateChain(upperLeg, lowerLeg, foot);

        // Final micro-alignment if plane twist drifted slightly.
        Vector3 solvedDir = foot.WorldTranslation - upperLeg.WorldTranslation;
        if (solvedDir.LengthSquared() > 1.0e-6f)
        {
            Quaternion microAlign = CreateShortestArcRotation(Vector3.Normalize(solvedDir), toTargetDir);
            upperLeg.SetWorldTranslationRotation(upperLeg.WorldTranslation, Quaternion.Normalize(microAlign * upperLeg.WorldRotation));
            RecalculateChain(upperLeg, lowerLeg, foot);
        }
    }

    private static void RecalculateChain(Transform upperLeg, Transform lowerLeg, Transform foot)
    {
        upperLeg.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
        lowerLeg.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
        foot.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
    }

    private static void AlignAnkleToNormal(Transform foot, Vector3 groundNormal, float weight, float maxTiltDegrees)
    {
        if (groundNormal.LengthSquared() < 1.0e-6f)
            return;

        Vector3 unitNormal = Vector3.Normalize(groundNormal);
        Quaternion tilt = CreateShortestArcRotation(Vector3.UnitY, unitNormal);
        float maxTiltRad = maxTiltDegrees * (MathF.PI / 180.0f);
        float angle = 2.0f * MathF.Acos(Math.Clamp(MathF.Abs(tilt.W), 0.0f, 1.0f));
        if (angle > maxTiltRad && angle > 1.0e-5f)
            tilt = Quaternion.Slerp(Quaternion.Identity, tilt, maxTiltRad / angle);

        Quaternion blendedTilt = Quaternion.Slerp(Quaternion.Identity, tilt, weight);
        foot.SetWorldTranslationRotation(foot.WorldTranslation, Quaternion.Normalize(blendedTilt * foot.WorldRotation));
        foot.RecalculateMatrices(true);
    }

    private static Quaternion CreateShortestArcRotation(Vector3 fromUnit, Vector3 toUnit)
    {
        float dot = Math.Clamp(Vector3.Dot(fromUnit, toUnit), -1.0f, 1.0f);
        if (dot > 0.99999f)
            return Quaternion.Identity;

        if (dot < -0.99999f)
        {
            Vector3 ortho = MathF.Abs(fromUnit.X) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(fromUnit, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(fromUnit, Vector3.UnitY));
            return Quaternion.CreateFromAxisAngle(ortho, MathF.PI);
        }

        Vector3 axis = Vector3.Cross(fromUnit, toUnit);
        return Quaternion.Normalize(new Quaternion(axis.X, axis.Y, axis.Z, 1.0f + dot));
    }
}
