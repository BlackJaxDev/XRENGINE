using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Occlusion;

/// <summary>Named members of the opt-in Phase 5.2.4b occlusion cohort.</summary>
public enum ECpuOcclusionValidationRole
{
    None,
    Occluder,
    HiddenTarget,
    StableVisibleSentinel,
    MovingVisibleSentinel,
    TopEdgeVisibleSentinel,
}

/// <summary>
/// One command-level observation from the bounded Phase 5.2.4b evidence ring.
/// Candidate membership is captured before the enabled decision, so a paired
/// occlusion-off run can prove enabled-set subset and final-image parity.
/// </summary>
public readonly record struct CpuOcclusionValidationEvidenceSnapshot(
    ulong FrameId,
    OcclusionViewKey ViewKey,
    uint StableQueryKey,
    ECpuOcclusionValidationRole Role,
    EOcclusionCullingMode Mode,
    bool CandidateObserved,
    bool Rendered,
    bool Culled,
    uint OcclusionProofCoverageMask,
    bool HasDecision,
    ECpuOcclusionDecision Decision);

/// <summary>
/// Fixed-capacity, allocation-free-in-steady-state command evidence used only
/// when XRE_VULKAN_PHASE524B_VALIDATION is enabled.
/// </summary>
public static class CpuOcclusionValidationEvidence
{
    public const string OccluderMaterialName = "Phase524bOccluderMaterial";
    public const string HiddenTargetMaterialName = "Phase524bHiddenTargetMaterial";
    public const string StableSentinelMaterialName = "Phase524bStableVisibleSentinelMaterial";
    public const string MovingSentinelMaterialName = "Phase524bMovingVisibleSentinelMaterial";
    public const string TopEdgeSentinelMaterialName = "Phase524bTopEdgeVisibleSentinelMaterial";
    public const string DesktopMovingSentinelMaterialName = "Phase524bDesktopMovingVisibleSentinelMaterial";
    public const string DesktopTopEdgeSentinelMaterialName = "Phase524bDesktopTopEdgeVisibleSentinelMaterial";
    public const string SpsMovingSentinelMaterialName = "Phase524bSpsMovingVisibleSentinelMaterial";
    public const string SpsTopEdgeSentinelMaterialName = "Phase524bSpsTopEdgeVisibleSentinelMaterial";
    public const int MaximumEntriesPerFrame = 512;

    private const int RingFrameCount = 4;
    private static readonly object s_lock = new();
    private static readonly FrameSlot[] s_slots =
    [
        new(),
        new(),
        new(),
        new(),
    ];
    private static readonly bool s_enabled = ReadEnabled();
    private static long s_overflowCount;

    private struct MutableEntry
    {
        public OcclusionViewKey ViewKey;
        public uint StableQueryKey;
        public ECpuOcclusionValidationRole Role;
        public EOcclusionCullingMode Mode;
        public bool CandidateObserved;
        public bool Rendered;
        public bool Culled;
        public uint OcclusionProofCoverageMask;
        public bool HasDecision;
        public ECpuOcclusionDecision Decision;
    }

    private sealed class FrameSlot
    {
        public ulong FrameId = ulong.MaxValue;
        public int Count;
        public readonly MutableEntry[] Entries = new MutableEntry[MaximumEntriesPerFrame];
    }

    public static bool Enabled => s_enabled;
    public static long OverflowCount => Interlocked.Read(ref s_overflowCount);

    public static ECpuOcclusionValidationRole ResolveRole(IRenderCommandMesh command)
    {
        if (!s_enabled)
            return ECpuOcclusionValidationRole.None;

        return ResolveRole((command.MaterialOverride ?? command.Mesh?.Material)?.Name);
    }

    public static bool IsApplicableToScope(IRenderCommandMesh command, EOcclusionViewScope scope)
    {
        if (!s_enabled)
            return false;

        return IsApplicableToScope((command.MaterialOverride ?? command.Mesh?.Material)?.Name, scope);
    }

    internal static ECpuOcclusionValidationRole ResolveRole(string? materialName)
        => materialName switch
        {
            OccluderMaterialName => ECpuOcclusionValidationRole.Occluder,
            HiddenTargetMaterialName => ECpuOcclusionValidationRole.HiddenTarget,
            StableSentinelMaterialName => ECpuOcclusionValidationRole.StableVisibleSentinel,
            MovingSentinelMaterialName => ECpuOcclusionValidationRole.MovingVisibleSentinel,
            TopEdgeSentinelMaterialName => ECpuOcclusionValidationRole.TopEdgeVisibleSentinel,
            DesktopMovingSentinelMaterialName => ECpuOcclusionValidationRole.MovingVisibleSentinel,
            DesktopTopEdgeSentinelMaterialName => ECpuOcclusionValidationRole.TopEdgeVisibleSentinel,
            SpsMovingSentinelMaterialName => ECpuOcclusionValidationRole.MovingVisibleSentinel,
            SpsTopEdgeSentinelMaterialName => ECpuOcclusionValidationRole.TopEdgeVisibleSentinel,
            _ => ECpuOcclusionValidationRole.None,
        };

    internal static bool IsKnownVisibleRole(ECpuOcclusionValidationRole role)
        => role is ECpuOcclusionValidationRole.Occluder
            or ECpuOcclusionValidationRole.StableVisibleSentinel
            or ECpuOcclusionValidationRole.MovingVisibleSentinel
            or ECpuOcclusionValidationRole.TopEdgeVisibleSentinel;

    internal static bool ShouldRenderInScope(
        ECpuOcclusionValidationRole role,
        bool scopeApplicable)
        => role == ECpuOcclusionValidationRole.None || scopeApplicable;

    internal static bool IsApplicableToScope(string? materialName, EOcclusionViewScope scope)
        => materialName switch
        {
            DesktopMovingSentinelMaterialName or DesktopTopEdgeSentinelMaterialName
                => scope is EOcclusionViewScope.MonoDesktop
                    or EOcclusionViewScope.EditorDesktopWhileVr
                    or EOcclusionViewScope.MirrorOnly,
            SpsMovingSentinelMaterialName or SpsTopEdgeSentinelMaterialName
                => scope is EOcclusionViewScope.VrSinglePassStereo
                    or EOcclusionViewScope.VrFoveatedView,
            _ => true,
        };

    public static void RecordCandidate(
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode)
    {
        if (!s_enabled)
            return;

        RecordCandidateCore(
            RuntimeEngine.Rendering.State.RenderFrameId,
            viewKey,
            stableQueryKey,
            role,
            mode);
    }

    public static void RecordRendered(
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode,
        ECpuOcclusionDecision decision)
    {
        if (!s_enabled)
            return;

        RecordOutcomeCore(
            RuntimeEngine.Rendering.State.RenderFrameId,
            viewKey,
            stableQueryKey,
            role,
            mode,
            rendered: true,
            culled: false,
            proofCoverageMask: 0u,
            decision: decision);
    }

    public static void RecordCulled(
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode,
        uint proofCoverageMask,
        ECpuOcclusionDecision decision)
    {
        if (!s_enabled)
            return;

        RecordOutcomeCore(
            RuntimeEngine.Rendering.State.RenderFrameId,
            viewKey,
            stableQueryKey,
            role,
            mode,
            rendered: false,
            culled: true,
            proofCoverageMask: proofCoverageMask,
            decision: decision);
    }

    /// <summary>Copies a specific frame. Destination should hold <see cref="MaximumEntriesPerFrame"/> entries.</summary>
    public static int CopyFrame(ulong frameId, Span<CpuOcclusionValidationEvidenceSnapshot> destination)
    {
        lock (s_lock)
        {
            FrameSlot? slot = FindSlot(frameId);
            if (slot is null)
                return 0;

            int count = Math.Min(slot.Count, destination.Length);
            for (int i = 0; i < count; i++)
                destination[i] = ToSnapshot(frameId, slot.Entries[i]);
            return count;
        }
    }

    /// <summary>Copies the newest frame currently retained by the four-frame ring.</summary>
    public static int CopyLatest(
        Span<CpuOcclusionValidationEvidenceSnapshot> destination,
        out ulong frameId)
    {
        lock (s_lock)
        {
            FrameSlot? latest = null;
            for (int i = 0; i < s_slots.Length; i++)
            {
                FrameSlot candidate = s_slots[i];
                if (candidate.FrameId == ulong.MaxValue ||
                    (latest is not null && candidate.FrameId <= latest.FrameId))
                {
                    continue;
                }

                latest = candidate;
            }

            if (latest is null)
            {
                frameId = 0UL;
                return 0;
            }

            frameId = latest.FrameId;
            int count = Math.Min(latest.Count, destination.Length);
            for (int i = 0; i < count; i++)
                destination[i] = ToSnapshot(frameId, latest.Entries[i]);
            return count;
        }
    }

    internal static void ResetForTests()
    {
        lock (s_lock)
        {
            for (int i = 0; i < s_slots.Length; i++)
            {
                s_slots[i].FrameId = ulong.MaxValue;
                s_slots[i].Count = 0;
                Array.Clear(s_slots[i].Entries);
            }

            Interlocked.Exchange(ref s_overflowCount, 0L);
        }
    }

    internal static void RecordCandidateForTests(
        ulong frameId,
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode)
        => RecordCandidateCore(frameId, viewKey, stableQueryKey, role, mode);

    internal static void RecordOutcomeForTests(
        ulong frameId,
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode,
        bool rendered,
        bool culled,
        uint proofCoverageMask,
        ECpuOcclusionDecision decision)
        => RecordOutcomeCore(
            frameId,
            viewKey,
            stableQueryKey,
            role,
            mode,
            rendered,
            culled,
            proofCoverageMask,
            decision);

    private static void RecordCandidateCore(
        ulong frameId,
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode)
    {
        lock (s_lock)
        {
            int index = FindOrAddEntry(frameId, viewKey, stableQueryKey, role, mode);
            if (index < 0)
                return;

            FrameSlot slot = GetSlot(frameId);
            slot.Entries[index].CandidateObserved = true;
        }
    }

    private static void RecordOutcomeCore(
        ulong frameId,
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode,
        bool rendered,
        bool culled,
        uint proofCoverageMask,
        ECpuOcclusionDecision decision)
    {
        lock (s_lock)
        {
            int index = FindOrAddEntry(frameId, viewKey, stableQueryKey, role, mode);
            if (index < 0)
                return;

            FrameSlot slot = GetSlot(frameId);
            ref MutableEntry entry = ref slot.Entries[index];
            entry.Rendered |= rendered;
            entry.Culled |= culled;
            entry.OcclusionProofCoverageMask |= proofCoverageMask;
            entry.HasDecision = true;
            entry.Decision = decision;
        }
    }

    private static int FindOrAddEntry(
        ulong frameId,
        in OcclusionViewKey viewKey,
        uint stableQueryKey,
        ECpuOcclusionValidationRole role,
        EOcclusionCullingMode mode)
    {
        FrameSlot slot = GetSlot(frameId);
        for (int i = 0; i < slot.Count; i++)
        {
            ref MutableEntry existing = ref slot.Entries[i];
            if (existing.StableQueryKey == stableQueryKey &&
                existing.Role == role &&
                existing.Mode == mode &&
                existing.ViewKey.Equals(viewKey))
            {
                return i;
            }
        }

        if (slot.Count >= MaximumEntriesPerFrame)
        {
            Interlocked.Increment(ref s_overflowCount);
            return -1;
        }

        int index = slot.Count++;
        slot.Entries[index] = new MutableEntry
        {
            ViewKey = viewKey,
            StableQueryKey = stableQueryKey,
            Role = role,
            Mode = mode,
        };
        return index;
    }

    private static FrameSlot GetSlot(ulong frameId)
    {
        FrameSlot slot = s_slots[(int)(frameId % RingFrameCount)];
        if (slot.FrameId == frameId)
            return slot;

        slot.FrameId = frameId;
        slot.Count = 0;
        return slot;
    }

    private static FrameSlot? FindSlot(ulong frameId)
    {
        FrameSlot slot = s_slots[(int)(frameId % RingFrameCount)];
        return slot.FrameId == frameId ? slot : null;
    }

    private static CpuOcclusionValidationEvidenceSnapshot ToSnapshot(
        ulong frameId,
        in MutableEntry entry)
        => new(
            frameId,
            entry.ViewKey,
            entry.StableQueryKey,
            entry.Role,
            entry.Mode,
            entry.CandidateObserved,
            entry.Rendered,
            entry.Culled,
            entry.OcclusionProofCoverageMask,
            entry.HasDecision,
            entry.Decision);

    private static bool ReadEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPhase524bValidation);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
