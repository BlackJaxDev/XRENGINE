using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Pure residency decisions for imported texture streaming. Callers provide a snapshot
/// of mutable runtime state; this type does not load files, mutate textures, or touch GL.
/// Thread-safety: all members are stateless and free-threaded.
/// </summary>
internal static class TextureResidencyPolicy
{
    internal const int RecentlyBoundFallbackFrames = 3;
    internal const uint BoundMaterialRelatedMinResidentMaxDimension = 512u;
    internal const float BoundMaterialFallbackProjectedPixelSpan = 384.0f;
    internal const float BoundMaterialFallbackScreenCoverage = 0.12f;
    internal const float UrgentVisibleProjectedPixelSpan = 512.0f;
    internal const float UrgentVisibleScreenCoverage = 0.08f;
    internal const float PageSelectionFullCoverageThreshold = 0.85f;
    internal const int PromotionCooldownFrames = 6;
    internal const int DemotionCooldownFrames = 90;
    internal const int VisiblePromotionPinFrames = 180;
    internal const int PromotionFadeFrames = 90;

    private static readonly uint[] ResidentCandidates =
    [
        1u,
        2u,
        4u,
        8u,
        16u,
        32u,
        64u,
        128u,
        256u,
        512u,
        1024u,
        2048u,
        4096u,
        8192u,
    ];

    // Kept conservative until page tracking is driven by actual material sampling domains.
    private static readonly bool EnablePartialSparsePageResidency = false;

    internal static uint DetermineDesiredResidentSize(
        ImportedTextureStreamingPolicyInput input,
        long frameId,
        bool allowPromotions,
        uint previewMaxDimension)
    {
        uint sourceMaxDimension = Math.Max(input.SourceWidth, input.SourceHeight);
        uint minimumResidentSize = XRTexture2D.GetMinimumResidentSize(sourceMaxDimension);
        uint previewResidentSize = sourceMaxDimension == 0
            ? previewMaxDimension
            : Math.Min(sourceMaxDimension, previewMaxDimension);
        uint normalPolicyFloor = Math.Max(minimumResidentSize, previewResidentSize);
        if (sourceMaxDimension != 0 && sourceMaxDimension <= minimumResidentSize)
            return sourceMaxDimension;

        if (!input.PreviewReady || sourceMaxDimension == 0)
            return previewResidentSize;

        if (!allowPromotions)
            return previewResidentSize;

        if (input.LastVisibleFrameId == frameId)
        {
            float roleMultiplier = ResolveTextureRoleMultiplier(input.SamplerName);
            float uvDensityHint = NormalizeUvDensityHint(input.UvDensityHint);
            float projectedPixelSpan = float.IsFinite(input.MaxProjectedPixelSpan)
                ? MathF.Max(0.0f, input.MaxProjectedPixelSpan)
                : 0.0f;
            float screenCoverage = float.IsFinite(input.MaxScreenCoverage)
                ? Math.Clamp(input.MaxScreenCoverage, 0.0f, 1.0f)
                : 0.0f;
            float targetPixelSpan = projectedPixelSpan * roleMultiplier * uvDensityHint;

            if (screenCoverage >= 0.95f)
                return sourceMaxDimension;

            uint visibleTarget = QuantizeResidentSize(sourceMaxDimension, normalPolicyFloor, targetPixelSpan);
            if (input.ResidentMaxDimension > visibleTarget)
            {
                uint nextLowerResidentSize = GetNextLowerResidentCandidate(sourceMaxDimension, input.ResidentMaxDimension, normalPolicyFloor);
                visibleTarget = Math.Max(visibleTarget, nextLowerResidentSize);
            }

            return Math.Max(normalPolicyFloor, visibleTarget);
        }

        long framesSinceVisible = input.LastVisibleFrameId < 0
            ? long.MaxValue
            : Math.Max(0L, frameId - input.LastVisibleFrameId);

        if (framesSinceVisible <= 4)
            return Math.Max(normalPolicyFloor, input.ResidentMaxDimension);

        if (framesSinceVisible <= 12)
            return GetNextLowerResidentCandidate(sourceMaxDimension, input.ResidentMaxDimension, normalPolicyFloor);

        return normalPolicyFloor;
    }

    internal static uint FitResidentSizeToBudget(
        ImportedTextureStreamingBudgetInput input,
        uint desiredResidentSize,
        long availableManagedBytes,
        ITextureResidencyBackend backend,
        ESizedInternalFormat format,
        int sparseNumLevels)
    {
        uint sourceMaxDimension = Math.Max(input.SourceWidth, input.SourceHeight);
        uint minimumResidentSize = XRTexture2D.GetMinimumResidentSize(sourceMaxDimension);
        uint candidate = Math.Max(minimumResidentSize, desiredResidentSize);

        if (availableManagedBytes == long.MaxValue)
            return candidate;

        while (candidate > minimumResidentSize)
        {
            long requiredBytes = backend.EstimateCommittedBytes(input.SourceWidth, input.SourceHeight, candidate, format, sparseNumLevels, input.PageSelection);
            if (requiredBytes <= availableManagedBytes)
                return candidate;

            candidate = backend.GetNextLowerResidentSize(sourceMaxDimension, candidate);
        }

        return minimumResidentSize;
    }

    internal static SparseTextureStreamingPageSelection DetermineDesiredPageSelection(
        ImportedTextureStreamingSnapshot snapshot,
        uint residentSize,
        long frameId)
    {
        if (!snapshot.Backend.SupportsSparseResidency)
            return SparseTextureStreamingPageSelection.Full;

        // Conservative for now: partial sparse page residency can expose black holes
        // when material UV transforms, wrapping, filtering, or rapid camera movement
        // sample outside the last recorded mesh UV rectangle. Keep sparse mip-level
        // residency, but fully commit the resident sparse mips until page tracking is
        // driven by the actual material sampling domain.
        if (!EnablePartialSparsePageResidency)
            return SparseTextureStreamingPageSelection.Full;

        uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
        uint previewResidentSize = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
        if (residentSize <= previewResidentSize
            || snapshot.LastVisibleFrameId != frameId
            || sourceMaxDimension < 2048u)
        {
            return SparseTextureStreamingPageSelection.Full;
        }

        return snapshot.VisiblePageSelection.Normalize(PageSelectionFullCoverageThreshold);
    }

    internal static int ComparePriority(
        ImportedTextureStreamingSnapshot left,
        ImportedTextureStreamingSnapshot right,
        long frameId)
    {
        bool leftVisible = left.LastVisibleFrameId == frameId;
        bool rightVisible = right.LastVisibleFrameId == frameId;
        if (leftVisible != rightVisible)
            return leftVisible ? -1 : 1;

        bool leftRecentlyBound = IsRecentlyBound(left, frameId);
        bool rightRecentlyBound = IsRecentlyBound(right, frameId);
        if (leftRecentlyBound != rightRecentlyBound)
            return leftRecentlyBound ? -1 : 1;

        if (leftVisible && rightVisible)
        {
            int priorityCompare = CalculatePriorityScore(right).CompareTo(CalculatePriorityScore(left));
            if (priorityCompare != 0)
                return priorityCompare;

            int distanceCompare = left.MinVisibleDistance.CompareTo(right.MinVisibleDistance);
            if (distanceCompare != 0)
                return distanceCompare;
        }

        int recencyCompare = right.LastVisibleFrameId.CompareTo(left.LastVisibleFrameId);
        if (recencyCompare != 0)
            return recencyCompare;

        int bindingRecencyCompare = right.LastBoundFrameId.CompareTo(left.LastBoundFrameId);
        if (bindingRecencyCompare != 0)
            return bindingRecencyCompare;

        return right.ResidentMaxDimension.CompareTo(left.ResidentMaxDimension);
    }

    internal static float CalculatePriorityScore(ImportedTextureStreamingSnapshot snapshot)
    {
        float projectedPixelSpan = snapshot.MaxProjectedPixelSpan;
        float screenCoverage = snapshot.MaxScreenCoverage;
        if (snapshot.LastBoundMaterialTextureCount > 1)
        {
            projectedPixelSpan = Math.Max(projectedPixelSpan, BoundMaterialFallbackProjectedPixelSpan);
            screenCoverage = Math.Max(screenCoverage, BoundMaterialFallbackScreenCoverage);
        }

        return (projectedPixelSpan + screenCoverage * 1024.0f)
            * ResolveTextureRoleMultiplier(snapshot.SamplerName)
            * NormalizeUvDensityHint(snapshot.UvDensityHint);
    }

    internal static JobPriority ResolveTransitionJobPriority(
        ImportedTextureStreamingSnapshot snapshot,
        long frameId,
        uint targetResidentSize,
        uint currentResidentSize,
        long currentCommittedBytes,
        long targetCommittedBytes,
        bool pressureDemotion)
    {
        if (pressureDemotion)
            return JobPriority.Low;

        bool isPromotion = targetResidentSize > currentResidentSize || targetCommittedBytes > currentCommittedBytes;
        bool visibleThisFrame = snapshot.LastVisibleFrameId == frameId;
        bool recentlyBound = IsRecentlyBound(snapshot, frameId);
        if (visibleThisFrame)
        {
            if (isPromotion
                && (snapshot.MaxProjectedPixelSpan >= UrgentVisibleProjectedPixelSpan
                    || snapshot.MaxScreenCoverage >= UrgentVisibleScreenCoverage
                    || targetResidentSize > snapshot.Backend.PreviewMaxDimension))
            {
                return JobPriority.Highest;
            }

            return JobPriority.High;
        }

        if (isPromotion && recentlyBound)
            return JobPriority.High;

        return isPromotion ? JobPriority.Normal : JobPriority.Low;
    }

    internal static TextureUploadPriorityClass ResolveUploadPriorityClass(JobPriority priority)
        => priority >= JobPriority.Highest
            ? TextureUploadPriorityClass.VisibleNow
            : priority >= JobPriority.High
                ? TextureUploadPriorityClass.NearVisible
                : priority <= JobPriority.Lowest
                    ? TextureUploadPriorityClass.Demotion
                    : TextureUploadPriorityClass.Background;

    internal static bool IsRecentlyBound(ImportedTextureStreamingSnapshot snapshot, long frameId)
        => snapshot.LastBoundFrameId != long.MinValue
            && frameId >= snapshot.LastBoundFrameId
            && frameId - snapshot.LastBoundFrameId <= RecentlyBoundFallbackFrames;

    internal static string ResolveTransitionReason(
        ImportedTextureStreamingSnapshot snapshot,
        long frameId,
        bool isPromotion,
        bool pressureDemotion,
        bool importsActive)
    {
        if (pressureDemotion)
            return "vram pressure demotion";

        if (isPromotion && snapshot.LastVisibleFrameId == frameId)
            return importsActive ? "visible import-era promotion" : "visible quality promotion";

        if (isPromotion && IsRecentlyBound(snapshot, frameId))
            return importsActive ? "bound import-era promotion" : "recent material binding promotion";

        if (isPromotion)
            return "recent visibility promotion";

        return importsActive ? "import-era demotion skipped" : "visibility grace demotion";
    }

    internal static string ResolveFairnessGroupKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        string? directory = Path.GetDirectoryName(filePath);
        return string.IsNullOrWhiteSpace(directory)
            ? Path.GetFileNameWithoutExtension(filePath) ?? string.Empty
            : directory;
    }

    internal static float CalculatePromotionFadeBias(uint sourceWidth, uint sourceHeight, uint previousResidentSize, uint nextResidentSize)
    {
        if (previousResidentSize == 0 || nextResidentSize == 0 || nextResidentSize <= previousResidentSize)
            return 0.0f;

        int previousBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, previousResidentSize);
        int nextBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, nextResidentSize);
        return Math.Max(0, previousBaseMipLevel - nextBaseMipLevel);
    }

    internal static float SmoothPromotionFadeProgress(float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        return t * t * (3.0f - (2.0f * t));
    }

    internal static float NormalizeUvDensityHint(float value)
        => float.IsFinite(value)
            ? Math.Clamp(value, 0.5f, 2.0f)
            : 1.0f;

    internal static float ResolveTextureRoleMultiplier(string? samplerName)
    {
        if (string.IsNullOrWhiteSpace(samplerName))
            return 1.0f;

        string normalized = samplerName.Trim().ToLowerInvariant();
        if (normalized.Contains("normal") || normalized.Contains("bump") || normalized.Contains("height") || normalized.Contains("parallax"))
            return 0.85f;

        if (normalized.Contains("rough") || normalized.Contains("metal") || normalized.Contains("occlusion") || normalized.Contains("ao") || normalized.Contains("orm") || normalized.Contains("specular"))
            return 0.65f;

        if (normalized.Contains("alpha") || normalized.Contains("mask") || normalized.Contains("opacity") || normalized.Contains("emissive"))
            return 0.95f;

        return 1.0f;
    }

    private static uint QuantizeResidentSize(uint sourceMaxDimension, uint minimumResidentSize, float targetPixelSpan)
    {
        if (!float.IsFinite(targetPixelSpan) || targetPixelSpan <= minimumResidentSize)
            return minimumResidentSize;

        uint clampedTarget = (uint)Math.Clamp(MathF.Ceiling(targetPixelSpan), minimumResidentSize, sourceMaxDimension);
        for (int i = 0; i < ResidentCandidates.Length; i++)
        {
            uint candidate = ResidentCandidates[i];
            if (candidate < clampedTarget)
                continue;

            return Math.Min(sourceMaxDimension, Math.Max(minimumResidentSize, candidate));
        }

        return sourceMaxDimension;
    }

    private static uint GetNextLowerResidentCandidate(uint sourceMaxDimension, uint currentResidentSize, uint minimumResidentSize)
    {
        if (currentResidentSize <= minimumResidentSize)
            return minimumResidentSize;

        for (int index = ResidentCandidates.Length - 1; index >= 0; index--)
        {
            uint candidate = ResidentCandidates[index];
            if (candidate >= currentResidentSize)
                continue;
            if (sourceMaxDimension != 0 && candidate > sourceMaxDimension)
                continue;

            return Math.Max(candidate, minimumResidentSize);
        }

        return minimumResidentSize;
    }
}
