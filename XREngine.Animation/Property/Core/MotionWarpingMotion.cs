using System;
using System.Collections.Generic;
using System.Numerics;
using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

/// <summary>
/// Wraps an authored <see cref="AnimationClip"/> (such as a vault, mantle, climb, or seat clip)
/// and dynamically warps its root translation and yaw deltas inside configured
/// <see cref="MotionWarpWindow"/> intervals so the character converges onto runtime
/// <see cref="MotionWarpTarget"/> transforms without clipping or floating.
/// </summary>
[MemoryPackable]
public partial class MotionWarpingMotion : MotionBase, IHumanoidProceduralRootMotionSource
{
    private AnimationClip? _sourceClip;
    private List<MotionWarpWindow> _warpWindows = [];

    [MemoryPackIgnore]
    private readonly List<MotionWarpTarget> _boundTargets = [];

    [MemoryPackIgnore]
    private Vector3 _canonicalBodyPosition;

    [MemoryPackIgnore]
    private Quaternion _canonicalBodyRotation = Quaternion.Identity;

    [MemoryPackIgnore]
    private Vector3 _currentBodyPosition;

    [MemoryPackIgnore]
    private Quaternion _currentBodyRotation = Quaternion.Identity;

    [MemoryPackIgnore]
    private Vector3 _startWorldPosition;

    [MemoryPackIgnore]
    private float _startWorldYaw;

    [MemoryPackIgnore]
    private bool _hasValidPose;

    public AnimationClip? SourceClip
    {
        get => _sourceClip;
        set => SetField(ref _sourceClip, value);
    }

    public List<MotionWarpWindow> WarpWindows
    {
        get => _warpWindows;
        set => SetField(ref _warpWindows, value ?? []);
    }

    [MemoryPackIgnore]
    public Vector3 CurrentWarpedBodyPosition => _currentBodyPosition;

    [MemoryPackIgnore]
    public Quaternion CurrentWarpedBodyRotation => _currentBodyRotation;

    internal override bool RequiresHumanoidAvatar => false;
    internal override bool IsProceduralMotion => true;

    internal override AnimationMember? EnsureProceduralBindingRootMember()
        => _sourceClip?.RootMember;

    internal override int PrepareProceduralHumanoidContributionCapacity()
        => _sourceClip is not null ? 1 : 0;

    internal override void OnProceduralSlotLayoutBound(
        AnimationSlotLayout layout,
        IReadOnlyDictionary<string, AnimSlot> slotsByPath)
    {
        if (_sourceClip is null)
            return;

        foreach ((string path, AnimationMember member) in _sourceClip.AnimatedCurves)
            if (slotsByPath.TryGetValue(path, out AnimSlot slot))
                member.Slot = slot;

        _sourceClip.SlotLayout = layout;
        _sourceClip.ValueStore.Resize(layout);
        _sourceClip.AnimatedMembersArray = [.. _sourceClip.AnimatedCurves.Values];
    }

    /// <summary>
    /// Sets the character's starting world root pose before playback enters this traversal motion.
    /// </summary>
    public void SetStartWorldRootPose(Vector3 worldPosition, float worldYawRadians)
    {
        _startWorldPosition = IsFinite(worldPosition) ? worldPosition : Vector3.Zero;
        _startWorldYaw = float.IsFinite(worldYawRadians) ? worldYawRadians : 0.0f;
        _canonicalBodyPosition = _startWorldPosition;
        _canonicalBodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _startWorldYaw);
        _currentBodyPosition = _canonicalBodyPosition;
        _currentBodyRotation = _canonicalBodyRotation;
    }

    /// <summary>
    /// Binds or updates a named runtime <see cref="MotionWarpTarget"/> without allocating when updating existing slots.
    /// </summary>
    public void SetWarpTarget(in MotionWarpTarget target)
    {
        string name = string.IsNullOrWhiteSpace(target.TargetName) ? "Default" : target.TargetName;
        for (int i = 0; i < _boundTargets.Count; i++)
        {
            if (string.Equals(_boundTargets[i].TargetName, name, StringComparison.OrdinalIgnoreCase))
            {
                _boundTargets[i] = target with { TargetName = name };
                return;
            }
        }

        _boundTargets.Add(target with { TargetName = name });
    }

    /// <summary>Removes a bound warp target by name so the clip falls back to unwarped playback.</summary>
    public void ClearWarpTarget(string targetName)
    {
        for (int i = _boundTargets.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_boundTargets[i].TargetName, targetName, StringComparison.OrdinalIgnoreCase))
                _boundTargets.RemoveAt(i);
        }
    }

    /// <summary>Removes all bound warp targets.</summary>
    public void ClearAllWarpTargets()
        => _boundTargets.Clear();

    internal override double GetProceduralDurationSeconds(IDictionary<string, AnimVar> variables)
        => _sourceClip is not null && float.IsFinite(_sourceClip.LengthInSeconds) && _sourceClip.LengthInSeconds > 0.0f
            ? _sourceClip.LengthInSeconds
            : 1.0;

    internal override void RestartProceduralPlayback(float startSeconds)
    {
        _sourceClip?.ResetImportedHumanoidStateClock(startSeconds);
        _canonicalBodyPosition = _startWorldPosition;
        _canonicalBodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _startWorldYaw);
        _currentBodyPosition = _canonicalBodyPosition;
        _currentBodyRotation = _canonicalBodyRotation;
        _hasValidPose = false;
    }

    internal override void StopProceduralPlayback()
        => _sourceClip?.ResetImportedHumanoidStateClock(0.0f);

    public override void Tick(float delta)
        => _sourceClip?.Tick(delta);

    public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
    {
        EvaluateProceduralMotionValues(variables, 0.0, additive: false);
        if (parentMotion is not null && SlotLayout is not null && !ReferenceEquals(parentMotion, this))
            parentMotion.ValueStore.CopyFrom(ValueStore);
    }

    internal override void EvaluateProceduralMotionValues(
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        bool additive)
    {
        if (_sourceClip is not null && _sourceClip.SlotLayout is not null)
        {
            _sourceClip.EvaluateClipAnimationValuesAtNormalizedStateTime(variables, normalizedTime, additive);
            if (SlotLayout is not null)
                ValueStore.CopyFrom(_sourceClip.ValueStore);
        }

        float normT = float.IsFinite((float)normalizedTime) ? Math.Clamp((float)normalizedTime, 0.0f, 1.0f) : 0.0f;
        EvaluateWarpedRootPose(normT, out _currentBodyPosition, out _currentBodyRotation);
        _hasValidPose = true;
    }

    /// <summary>
    /// Computes the warped world-space root position and rotation at normalized clip time <paramref name="normalizedTime"/>.
    /// Safe to call directly in deterministic tests.
    /// </summary>
    public void EvaluateWarpedRootPose(
        float normalizedTime,
        out Vector3 warpedPosition,
        out Quaternion warpedRotation)
    {
        float t = Math.Clamp(normalizedTime, 0.0f, 1.0f);
        Vector3 position = _startWorldPosition;
        float yaw = _startWorldYaw;

        for (int i = 0; i < _warpWindows.Count; i++)
        {
            MotionWarpWindow window = _warpWindows[i];
            if (!TryGetBoundTarget(window.TargetName, out MotionWarpTarget target) || !target.HasTarget)
                continue;

            float start = window.ClampedStart;
            float end = window.ClampedEnd;
            if (t < start)
                continue;

            float windowAlpha = t >= end ? 1.0f : (t - start) / MathF.Max(1.0e-5f, end - start);
            float smooth = windowAlpha * windowAlpha * (3.0f - 2.0f * windowAlpha);
            float blend = smooth * window.ClampedWeight;

            if (window.WarpTranslationXZ)
            {
                position.X += (target.Position.X - position.X) * blend;
                position.Z += (target.Position.Z - position.Z) * blend;
            }

            if (window.WarpTranslationY)
                position.Y += (target.Position.Y - position.Y) * blend;

            if (window.WarpYaw)
            {
                float targetYaw = ExtractYaw(target.Rotation);
                float yawDelta = WrapAngleRadians(targetYaw - yaw);
                yaw = WrapAngleRadians(yaw + yawDelta * blend);
            }
        }

        warpedPosition = position;
        warpedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    public bool TryGetBodySamples(
        out Vector3 canonicalBodyPosition,
        out Quaternion canonicalBodyRotation,
        out Vector3 currentBodyPosition,
        out Quaternion currentBodyRotation)
    {
        if (!_hasValidPose)
        {
            canonicalBodyPosition = Vector3.Zero;
            canonicalBodyRotation = Quaternion.Identity;
            currentBodyPosition = Vector3.Zero;
            currentBodyRotation = Quaternion.Identity;
            return false;
        }

        canonicalBodyPosition = _canonicalBodyPosition;
        canonicalBodyRotation = _canonicalBodyRotation;
        currentBodyPosition = _currentBodyPosition;
        currentBodyRotation = _currentBodyRotation;
        return true;
    }

    public void CopyProjectionMuscles(Span<float> destination)
        => destination.Clear();

    internal override bool TryCreateProceduralHumanoidContribution(
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        out HumanoidMotionContribution contribution)
    {
        if (!_hasValidPose || !float.IsFinite(weight) || weight <= 0.0f)
        {
            contribution = default;
            return false;
        }

        contribution = new HumanoidMotionContribution(
            Clip: null,
            Policy: MotionBricksLocomotionMotion.DefaultRootMotionPolicy with { BakePositionYIntoPose = false },
            OccurrenceId: occurrenceId,
            LifecycleGeneration: lifecycleGeneration,
            Weight: weight,
            SampleTime: 0.0f,
            SamplePhase: 0.0f,
            PlaybackLoopCycle: 0L,
            SourceLoopCycle: 0L,
            ContributionType: EHumanoidMotionContributionType.Override,
            ProceduralSource: this);
        return true;
    }

    private bool TryGetBoundTarget(string targetName, out MotionWarpTarget target)
    {
        string lookup = string.IsNullOrWhiteSpace(targetName) ? "Default" : targetName;
        for (int i = 0; i < _boundTargets.Count; i++)
        {
            if (string.Equals(_boundTargets[i].TargetName, lookup, StringComparison.OrdinalIgnoreCase))
            {
                target = _boundTargets[i];
                return true;
            }
        }

        target = default;
        return false;
    }

    private static float ExtractYaw(Quaternion rotation)
    {
        Quaternion normalized = rotation.LengthSquared() > 1.0e-8f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, normalized);
        float lenSq = forward.X * forward.X + forward.Z * forward.Z;
        return lenSq > 1.0e-8f ? MathF.Atan2(forward.X, forward.Z) : 0.0f;
    }

    private static float WrapAngleRadians(float radians)
    {
        if (!float.IsFinite(radians))
            return 0.0f;

        radians = (radians + MathF.PI) % MathF.Tau;
        if (radians < 0.0f)
            radians += MathF.Tau;
        return radians - MathF.PI;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
