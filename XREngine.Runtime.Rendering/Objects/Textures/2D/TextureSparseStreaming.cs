using System;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public enum ESparseTextureStreamingPageResidencyMode
{
    Full = 0,
    Partial = 1,
}

public readonly record struct SparseTextureStreamingPageSelection(
    ESparseTextureStreamingPageResidencyMode Mode,
    float MinU,
    float MinV,
    float MaxU,
    float MaxV)
{
    public static SparseTextureStreamingPageSelection Full
        => new(ESparseTextureStreamingPageResidencyMode.Full, 0.0f, 0.0f, 1.0f, 1.0f);

    public bool IsPartial
        => Mode == ESparseTextureStreamingPageResidencyMode.Partial
        && float.IsFinite(MinU)
        && float.IsFinite(MinV)
        && float.IsFinite(MaxU)
        && float.IsFinite(MaxV)
        && MaxU > MinU
        && MaxV > MinV;

    public float CoverageFraction
        => IsPartial
            ? Math.Clamp((MaxU - MinU) * (MaxV - MinV), 0.0f, 1.0f)
            : 1.0f;

    public static SparseTextureStreamingPageSelection Partial(float minU, float minV, float maxU, float maxV)
        => new(ESparseTextureStreamingPageResidencyMode.Partial, minU, minV, maxU, maxV);

    public SparseTextureStreamingPageSelection Normalize(float fullCoverageThreshold = 0.85f)
    {
        if (!IsPartial)
            return Full;

        float minU = Math.Clamp(MinU, 0.0f, 1.0f);
        float minV = Math.Clamp(MinV, 0.0f, 1.0f);
        float maxU = Math.Clamp(MaxU, 0.0f, 1.0f);
        float maxV = Math.Clamp(MaxV, 0.0f, 1.0f);
        if (!(maxU > minU) || !(maxV > minV))
            return Full;

        SparseTextureStreamingPageSelection normalized = new(ESparseTextureStreamingPageResidencyMode.Partial, minU, minV, maxU, maxV);
        return normalized.CoverageFraction >= fullCoverageThreshold
            ? Full
            : normalized;
    }

    public bool NearlyEquals(SparseTextureStreamingPageSelection other, float epsilon = 0.01f)
    {
        SparseTextureStreamingPageSelection left = Normalize();
        SparseTextureStreamingPageSelection right = other.Normalize();
        if (!left.IsPartial || !right.IsPartial)
            return !left.IsPartial && !right.IsPartial;

        return MathF.Abs(left.MinU - right.MinU) <= epsilon
            && MathF.Abs(left.MinV - right.MinV) <= epsilon
            && MathF.Abs(left.MaxU - right.MaxU) <= epsilon
            && MathF.Abs(left.MaxV - right.MaxV) <= epsilon;
    }

    public SparseTextureStreamingPageSelection Union(SparseTextureStreamingPageSelection other)
    {
        SparseTextureStreamingPageSelection left = Normalize();
        SparseTextureStreamingPageSelection right = other.Normalize();
        if (!left.IsPartial || !right.IsPartial)
            return Full;

        return Partial(
            MathF.Min(left.MinU, right.MinU),
            MathF.Min(left.MinV, right.MinV),
            MathF.Max(left.MaxU, right.MaxU),
            MathF.Max(left.MaxV, right.MaxV)).Normalize();
    }
}

public readonly record struct SparseTextureStreamingPageRegion(
    int MipLevel,
    int XOffset,
    int YOffset,
    uint Width,
    uint Height)
{
    public bool HasArea => Width > 0 && Height > 0;
}

public readonly record struct SparseTextureStreamingSupport(
    bool SupportsSparseTextures,
    bool SupportsSparseTexture2,
    uint VirtualPageSizeX,
    uint VirtualPageSizeY,
    int VirtualPageSizeIndex,
    string? FailureReason = null)
{
    public bool IsAvailable
        => SupportsSparseTextures
        && VirtualPageSizeX > 0
        && VirtualPageSizeY > 0;

    public bool IsPageAligned(uint width, uint height)
    {
        if (!IsAvailable)
            return false;

        return width > 0
            && height > 0
            && width % VirtualPageSizeX == 0
            && height % VirtualPageSizeY == 0;
    }

    public static SparseTextureStreamingSupport Unsupported(string? reason = null)
        => new(
            SupportsSparseTextures: false,
            SupportsSparseTexture2: false,
            VirtualPageSizeX: 0,
            VirtualPageSizeY: 0,
            VirtualPageSizeIndex: 0,
            FailureReason: reason);
}

public readonly record struct SparseTextureStreamingTransitionRequest(
    uint LogicalWidth,
    uint LogicalHeight,
    ESizedInternalFormat SizedInternalFormat,
    int LogicalMipCount,
    int RequestedBaseMipLevel,
    Mipmap2D[] ResidentMipmaps,
    SparseTextureStreamingPageSelection PageSelection = default);

public readonly record struct SparseTextureStreamingTransitionResult(
    bool Applied,
    bool UsedSparseResidency,
    int RequestedBaseMipLevel,
    int CommittedBaseMipLevel,
    int NumSparseLevels,
    long CommittedBytes,
    bool ExposureDeferred = false,
    nint FenceSync = 0,
    string? FailureReason = null)
{
    public static SparseTextureStreamingTransitionResult Unsupported(string? reason = null)
        => new(
            Applied: false,
            UsedSparseResidency: false,
            RequestedBaseMipLevel: 0,
            CommittedBaseMipLevel: 0,
            NumSparseLevels: 0,
            CommittedBytes: 0L,
            ExposureDeferred: false,
            FenceSync: 0,
            FailureReason: reason);
}

public readonly record struct SparseTextureStreamingFinalizeResult(
    bool Completed,
    bool Succeeded,
    string? FailureReason = null)
{
    public static SparseTextureStreamingFinalizeResult Pending()
        => new(
            Completed: false,
            Succeeded: true,
            FailureReason: null);

    public static SparseTextureStreamingFinalizeResult Success()
        => new(
            Completed: true,
            Succeeded: true,
            FailureReason: null);

    public static SparseTextureStreamingFinalizeResult Failed(string? reason = null)
        => new(
            Completed: true,
            Succeeded: false,
            FailureReason: reason);
}
