using System;

namespace XREngine.Rendering;

/// <summary>
/// Canonical key identifying a distinct GPU material classification workgroup.
/// </summary>
public readonly struct AdvancedClassificationKey : IEquatable<AdvancedClassificationKey>
{
    public uint ShadingKernelId { get; }
    public ulong MaterialLayoutHash { get; }
    public uint CoverageClass { get; }
    public uint DerivativeMode { get; }
    public uint ViewMode { get; }

    public AdvancedClassificationKey(
        uint shadingKernelId,
        ulong materialLayoutHash,
        uint coverageClass,
        uint derivativeMode,
        uint viewMode)
    {
        ShadingKernelId = shadingKernelId;
        MaterialLayoutHash = materialLayoutHash;
        CoverageClass = coverageClass;
        DerivativeMode = derivativeMode;
        ViewMode = viewMode;
    }

    public bool Equals(AdvancedClassificationKey other)
        => ShadingKernelId == other.ShadingKernelId &&
           MaterialLayoutHash == other.MaterialLayoutHash &&
           CoverageClass == other.CoverageClass &&
           DerivativeMode == other.DerivativeMode &&
           ViewMode == other.ViewMode;

    public override bool Equals(object? obj)
        => obj is AdvancedClassificationKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(ShadingKernelId, MaterialLayoutHash, CoverageClass, DerivativeMode, ViewMode);

    public static bool operator ==(AdvancedClassificationKey left, AdvancedClassificationKey right)
        => left.Equals(right);

    public static bool operator !=(AdvancedClassificationKey left, AdvancedClassificationKey right)
        => !left.Equals(right);
}
