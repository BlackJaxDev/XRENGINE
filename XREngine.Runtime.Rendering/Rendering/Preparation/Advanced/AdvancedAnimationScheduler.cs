using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity visibility-aware render-pose scheduler. Stable handles,
/// deterministic phase staggering, and accumulated delta make the warmed path
/// allocation-free and independent of synchronous GPU feedback.
/// </summary>
public sealed class AdvancedAnimationScheduler
{
    private readonly AdvancedGpuHandle[] _handles;
    private readonly ulong[] _lastPoseFrames;
    private readonly ulong[] _lastSeenFrames;
    private readonly float[] _accumulatedDeltaSeconds;
    private readonly uint[] _selectedBoneTiers;

    public AdvancedAnimationScheduler(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        int tableCapacity = NextPowerOfTwo(checked(capacity * 2));
        _handles = new AdvancedGpuHandle[tableCapacity];
        _lastPoseFrames = new ulong[tableCapacity];
        _lastSeenFrames = new ulong[tableCapacity];
        _accumulatedDeltaSeconds = new float[tableCapacity];
        _selectedBoneTiers = new uint[tableCapacity];
        Capacity = capacity;
    }

    public int Capacity { get; }

    public AdvancedAnimationScheduleDecision Schedule(
        in AdvancedAnimationVisibilityFeedback feedback,
        in AdvancedAnimationScheduleProfile profile,
        ReadOnlySpan<AdvancedBoneLodTier> boneTiers,
        EAdvancedAnimationBoneRequirement requiredOutputs,
        uint runtimeRequiredBoneCount,
        uint requestedBoneTier,
        ulong frameId,
        float deltaSeconds,
        bool gameplayCpuAnimationRequired,
        bool hasRenderConsumers = true)
    {
        if (!feedback.Entity.IsValid)
            throw new ArgumentException("Animation feedback requires a stable entity handle.", nameof(feedback));
        if (deltaSeconds < 0.0f || !float.IsFinite(deltaSeconds))
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

        int slot = FindOrAddSlot(feedback.Entity, frameId);
        if (slot < 0)
        {
            return new AdvancedAnimationScheduleDecision(
                UpdateRenderPose: false,
                gameplayCpuAnimationRequired,
                CadenceFrames: 0u,
                BoneTier: 0u,
                StalePoseAge: 0u,
                AccumulatedDeltaSeconds: deltaSeconds,
                SkipReason: EAdvancedAnimationSkipReason.SchedulerCapacity);
        }

        _lastSeenFrames[slot] = frameId;
        _accumulatedDeltaSeconds[slot] += deltaSeconds;

        ulong visibleAge = frameId >= feedback.LastVisibleFrame
            ? frameId - feedback.LastVisibleFrame
            : 0UL;
        bool directlyRelevant =
            (feedback.Flags &
             (EAdvancedAnimationVisibilityFlags.Visible |
              EAdvancedAnimationVisibilityFlags.ShadowRelevant)) != 0;
        bool inVisibilityGrace =
            directlyRelevant || visibleAge <= profile.VisibilityGraceFrames;
        uint cadence = profile.ResolveInterval(
            Math.Max(0.0f, feedback.ProjectedDiameter),
            inVisibilityGrace);
        uint stalePoseAge = ToUIntSaturated(
            frameId >= _lastPoseFrames[slot]
                ? frameId - _lastPoseFrames[slot]
                : 0UL);
        uint phase = StablePhase(feedback.Entity, cadence);
        bool phaseDue = cadence <= 1u || frameId % cadence == phase;
        bool staleDue =
            profile.MaximumStalePoseFrames != 0u &&
            stalePoseAge >= profile.MaximumStalePoseFrames;
        bool newlyVisible =
            (feedback.Flags &
             EAdvancedAnimationVisibilityFlags.NewlyVisible) != 0;
        bool updateRenderPose =
            hasRenderConsumers &&
            (newlyVisible || phaseDue || staleDue) &&
            (inVisibilityGrace || staleDue);

        uint boneTier = SelectBoneTier(
            boneTiers,
            requestedBoneTier,
            runtimeRequiredBoneCount,
            requiredOutputs);
        _selectedBoneTiers[slot] = boneTier;

        EAdvancedAnimationSkipReason skipReason = EAdvancedAnimationSkipReason.None;
        float accumulatedDelta = _accumulatedDeltaSeconds[slot];
        if (updateRenderPose)
        {
            _lastPoseFrames[slot] = frameId;
            _accumulatedDeltaSeconds[slot] = 0.0f;
            stalePoseAge = 0u;
        }
        else if (!hasRenderConsumers)
        {
            skipReason = EAdvancedAnimationSkipReason.NoRenderConsumers;
        }
        else if (!inVisibilityGrace)
        {
            skipReason = EAdvancedAnimationSkipReason.OutsideVisibilityGrace;
        }
        else
        {
            skipReason = EAdvancedAnimationSkipReason.Cadence;
        }

        return new AdvancedAnimationScheduleDecision(
            updateRenderPose,
            gameplayCpuAnimationRequired,
            cadence,
            boneTier,
            stalePoseAge,
            accumulatedDelta,
            skipReason);
    }

    private int FindOrAddSlot(AdvancedGpuHandle handle, ulong frameId)
    {
        uint mask = checked((uint)_handles.Length - 1u);
        uint start = Hash(handle) & mask;
        for (uint probe = 0u; probe < (uint)_handles.Length; probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            AdvancedGpuHandle existing = _handles[slot];
            if (existing == handle)
                return slot;
            if (existing.IsValid)
                continue;

            _handles[slot] = handle;
            _lastPoseFrames[slot] = frameId;
            _lastSeenFrames[slot] = frameId;
            _accumulatedDeltaSeconds[slot] = 0.0f;
            _selectedBoneTiers[slot] = 0u;
            return slot;
        }

        return -1;
    }

    private static uint SelectBoneTier(
        ReadOnlySpan<AdvancedBoneLodTier> tiers,
        uint requestedTier,
        uint runtimeRequiredBoneCount,
        EAdvancedAnimationBoneRequirement requirements)
    {
        if (tiers.IsEmpty)
            return 0u;

        int requested = Math.Min(
            checked((int)requestedTier),
            tiers.Length - 1);
        for (int tierIndex = requested; tierIndex >= 0; tierIndex--)
        {
            AdvancedBoneLodTier tier = tiers[tierIndex];
            if (tier.BoneCount < runtimeRequiredBoneCount)
                continue;
            if ((tier.PreservedRequirements & requirements) != requirements)
                continue;
            return checked((uint)tierIndex);
        }

        return 0u;
    }

    private static uint StablePhase(AdvancedGpuHandle handle, uint cadence)
    {
        if (cadence <= 1u)
            return 0u;
        return Hash(handle) % cadence;
    }

    private static uint Hash(AdvancedGpuHandle handle)
    {
        uint value = handle.Index * 0x9E3779B9u;
        value ^= handle.Generation + 0x85EBCA6Bu + (value << 6) + (value >> 2);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        return value;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result = checked(result << 1);
        return result;
    }

    private static uint ToUIntSaturated(ulong value)
        => value > uint.MaxValue ? uint.MaxValue : (uint)value;
}
