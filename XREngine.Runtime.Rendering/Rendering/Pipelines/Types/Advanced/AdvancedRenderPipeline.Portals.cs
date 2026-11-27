using System.Numerics;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private const int PortalTransformHistoryCapacity =
        AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame * 2;

    private struct PortalTransformHistoryEntry
    {
        public ulong ProviderId;
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 PortalTransform;
        public Vector3 PlanePoint;
        public Vector3 PlaneNormal;
    }

    private readonly AdvancedPortalViewScheduler _portalViewScheduler = new();
    private readonly AdvancedPortalFramePlan _portalFramePlan = new();
    private readonly AdvancedPortalSurfaceDescriptor[] _portalSurfaceScratch =
        new AdvancedPortalSurfaceDescriptor[PortalTransformHistoryCapacity];
    private readonly PortalTransformHistoryEntry[] _committedPortalHistory =
        new PortalTransformHistoryEntry[PortalTransformHistoryCapacity];
    private readonly PortalTransformHistoryEntry[] _currentPortalHistory =
        new PortalTransformHistoryEntry[PortalTransformHistoryCapacity];
    private int _committedPortalHistoryCount;
    private int _currentPortalHistoryCount;
    private ulong _lastPortalHistoryFrameGeneration;
    private bool _hasPortalHistoryFrame;
    private uint _portalFroxelSlotBudget;

    /// <summary>Bounded scheduling policy for in-place stencil portal and mirror sub-views.</summary>
    public AdvancedPortalSchedulerOptions PortalSchedulerOptions { get; set; } =
        AdvancedPortalSchedulerOptions.Default;

    /// <summary>Most recently scheduled portal sub-view plan for this pipeline.</summary>
    public AdvancedPortalFramePlan PortalFramePlan => _portalFramePlan;

    /// <summary>
    /// Number of extra froxel grid view slices reserved for active portal sub-views in the
    /// current resource profile. Remains zero when the scene has no registered portal surfaces.
    /// </summary>
    internal uint PortalFroxelSlotBudget => _portalFroxelSlotBudget;

    /// <summary>
    /// Captures active portal surfaces from <paramref name="publisher"/>, pairs each surface
    /// with this pipeline's own previous-frame portal transform for accurate reflected velocity,
    /// and schedules reflected sub-views against <paramref name="primaryViews"/> without heap allocation.
    /// </summary>
    internal ReadOnlySpan<AdvancedViewRecord> SchedulePortalViews(
        ReadOnlySpan<AdvancedViewRecord> primaryViews,
        Commands.AdvancedGpuScenePublisher? publisher)
    {
        if (primaryViews.IsEmpty || publisher is null || publisher.RegisteredPortalSurfaceCount == 0)
        {
            _portalFramePlan.Reset();
            _portalFramePlan.PrimaryViewCount = Math.Min(primaryViews.Length, RenderFrameViewSet.MaxViewCount);
            _currentPortalHistoryCount = 0;
            return ReadOnlySpan<AdvancedViewRecord>.Empty;
        }

        ulong frameGeneration = primaryViews[0].ViewGeneration;
        bool primaryHistoryValid = (primaryViews[0].Flags & EAdvancedViewRecordFlags.TemporalHistoryValid) != 0;
        if (_hasPortalHistoryFrame && frameGeneration != _lastPortalHistoryFrameGeneration)
        {
            if (_currentPortalHistoryCount > 0)
                Array.Copy(_currentPortalHistory, _committedPortalHistory, _currentPortalHistoryCount);
            _committedPortalHistoryCount = _currentPortalHistoryCount;
        }
        if (!primaryHistoryValid)
            _committedPortalHistoryCount = 0;
        _lastPortalHistoryFrameGeneration = frameGeneration;
        _hasPortalHistoryFrame = true;

        AdvancedPortalSchedulerOptions clampedOptions = PortalSchedulerOptions.ClampToHardLimits();
        EnsurePortalFroxelSlotBudget(checked((uint)clampedOptions.MaxTotalPortalViewsPerFrame));

        int surfaceCount = publisher.CaptureActivePortalSurfaces(_portalSurfaceScratch);
        if (surfaceCount == 0)
        {
            _portalFramePlan.Reset();
            _portalFramePlan.PrimaryViewCount = Math.Min(primaryViews.Length, RenderFrameViewSet.MaxViewCount);
            _currentPortalHistoryCount = 0;
            return ReadOnlySpan<AdvancedViewRecord>.Empty;
        }

        // Pair each portal surface with the transform that this pipeline rendered in its
        // previous frame. Tracking history per pipeline prevents desktop, XR stereo, and
        // update-thread pose writes from overwriting each other's previous-frame matrices.
        for (int index = 0; index < surfaceCount; ++index)
        {
            ref AdvancedPortalSurfaceDescriptor surface = ref _portalSurfaceScratch[index];
            int previousIndex = FindCommittedPortalHistoryIndex(surface.ProviderId);
            if (previousIndex >= 0)
            {
                ref readonly PortalTransformHistoryEntry previous = ref _committedPortalHistory[previousIndex];
                surface = surface with
                {
                    PreviousWorldMatrix = previous.WorldMatrix,
                    PreviousPortalTransform = previous.PortalTransform,
                    PreviousPlanePoint = previous.PlanePoint,
                    PreviousPlaneNormal = previous.PlaneNormal,
                };
            }
            else
            {
                surface = surface with
                {
                    PreviousWorldMatrix = surface.WorldMatrix,
                    PreviousPortalTransform = surface.PortalTransform,
                    PreviousPlanePoint = surface.PlanePoint,
                    PreviousPlaneNormal = surface.PlaneNormal,
                };
            }

            _currentPortalHistory[index] = new PortalTransformHistoryEntry
            {
                ProviderId = surface.ProviderId,
                WorldMatrix = surface.WorldMatrix,
                PortalTransform = surface.PortalTransform,
                PlanePoint = surface.PlanePoint,
                PlaneNormal = surface.PlaneNormal,
            };
        }
        _currentPortalHistoryCount = surfaceCount;

        _portalViewScheduler.Schedule(
            primaryViews,
            _portalSurfaceScratch.AsSpan(0, surfaceCount),
            clampedOptions,
            _portalFramePlan);
        return _portalFramePlan.PortalViewRecords;
    }

    private int FindCommittedPortalHistoryIndex(ulong providerId)
    {
        for (int index = 0; index < _committedPortalHistoryCount; ++index)
            if (_committedPortalHistory[index].ProviderId == providerId)
                return index;
        return -1;
    }

    private void EnsurePortalFroxelSlotBudget(uint requiredSlots)
    {
        uint clamped = Math.Min(requiredSlots, (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame);
        // Keep the froxel slice budget sticky once grown so temporary mirror toggles
        // (such as starting and ending VR calibration) do not rebuild pipeline resources.
        if (clamped <= _portalFroxelSlotBudget)
            return;
        _portalFroxelSlotBudget = clamped;
        InvalidateNativeShadingResourceProfile();
    }
}
