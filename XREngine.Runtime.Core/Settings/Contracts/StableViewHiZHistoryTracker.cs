namespace XREngine;

/// <summary>
/// Fixed-capacity stable-view Hi-Z history metadata. GPU texture ownership stays
/// with the backend; this tracker prevents histories migrating between views.
/// </summary>
public sealed class StableViewHiZHistoryTracker
{
    private readonly Entry[] _entries;
    private readonly uint _validationInterval;
    private uint _frameIndex;

    public StableViewHiZHistoryTracker(int capacity = RenderFrameViewSet.MaxViewCount, uint validationInterval = 120)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _entries = new Entry[capacity];
        _validationInterval = Math.Max(1u, validationInterval);
    }

    public void BeginFrame(uint frameIndex) => _frameIndex = frameIndex;

    public StableViewHiZDecision Resolve(in StableViewHiZRequest request)
    {
        EHiZHistoryInvalidation invalidation = ResolveInvalidation(request);
        bool periodicValidation = _frameIndex % _validationInterval == 0u;
        if (periodicValidation)
            invalidation |= EHiZHistoryInvalidation.PeriodicValidation;

        ulong sampleKey = request.IsInset ? request.StableOuterEyeKey : request.StableViewKey;
        uint sampleViewId = request.IsInset ? request.OuterEyeLogicalViewId : request.LogicalViewId;
        if (request.IsInset && (!request.OuterContainsInset || !request.ProjectionCompatible || !request.DepthConventionCompatible))
            invalidation |= EHiZHistoryInvalidation.InsetRelationshipUnproven;

        int entryIndex = Find(sampleKey);
        if (entryIndex < 0)
            invalidation |= EHiZHistoryInvalidation.MissingHistory;
        else
        {
            if (_entries[entryIndex].ResourceGeneration != request.ResourceGeneration)
                invalidation |= EHiZHistoryInvalidation.ResourceGenerationChanged;
            if (_entries[entryIndex].SceneRevision != request.SceneRevision)
                invalidation |= EHiZHistoryInvalidation.UnsafeSceneRevision;
        }

        EHiZOcclusionDisposition disposition = ResolveDisposition(request, invalidation);
        return new(
            request.StableViewKey,
            sampleKey,
            request.LogicalViewId,
            sampleViewId,
            disposition,
            invalidation,
            request.ExactViewProjection,
            request.IsInset ? request.OuterEyeViewProjection : request.ExactViewProjection);
    }

    public void Publish(ulong stableViewKey, ulong resourceGeneration, ulong sceneRevision)
    {
        int index = Find(stableViewKey);
        if (index < 0)
            index = FindReplacement();

        _entries[index] = new(stableViewKey, resourceGeneration, sceneRevision, _frameIndex, true);
    }

    public void Invalidate(ulong stableViewKey)
    {
        int index = Find(stableViewKey);
        if (index >= 0)
            _entries[index].Valid = false;
    }

    private EHiZHistoryInvalidation ResolveInvalidation(in StableViewHiZRequest request)
    {
        EHiZHistoryInvalidation result = EHiZHistoryInvalidation.None;
        if (request.CameraCut)
            result |= EHiZHistoryInvalidation.CameraCut;
        if (request.TrackingJump)
            result |= EHiZHistoryInvalidation.TrackingJump;
        if (request.ProjectionDiscontinuity)
            result |= EHiZHistoryInvalidation.ProjectionDiscontinuity;
        if (request.UnsafeSceneRevision)
            result |= EHiZHistoryInvalidation.UnsafeSceneRevision;
        return result;
    }

    private static EHiZOcclusionDisposition ResolveDisposition(
        in StableViewHiZRequest request,
        EHiZHistoryInvalidation invalidation)
    {
        if (request.RequestedMode == EHiZHistoryMode.Disabled)
            return EHiZOcclusionDisposition.Disabled;
        if (invalidation != EHiZHistoryInvalidation.None)
            return EHiZOcclusionDisposition.BypassConservatively;
        if (request.RequestedMode == EHiZHistoryMode.CurrentFrame && !request.IsInset)
            return EHiZOcclusionDisposition.BuildCurrentFrameOuterEye;
        return request.IsInset
            ? EHiZOcclusionDisposition.SampleValidatedOuterEyeHistory
            : EHiZOcclusionDisposition.SampleOwnHistory;
    }

    private int Find(ulong stableViewKey)
    {
        for (int i = 0; i < _entries.Length; i++)
            if (_entries[i].Valid && _entries[i].StableViewKey == stableViewKey)
                return i;
        return -1;
    }

    private int FindReplacement()
    {
        int oldest = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Valid)
                return i;
            if (_entries[i].LastPublishedFrame < _entries[oldest].LastPublishedFrame)
                oldest = i;
        }
        return oldest;
    }

    private struct Entry(
        ulong stableViewKey,
        ulong resourceGeneration,
        ulong sceneRevision,
        uint lastPublishedFrame,
        bool valid)
    {
        public ulong StableViewKey = stableViewKey;
        public ulong ResourceGeneration = resourceGeneration;
        public ulong SceneRevision = sceneRevision;
        public uint LastPublishedFrame = lastPublishedFrame;
        public bool Valid = valid;
    }
}
