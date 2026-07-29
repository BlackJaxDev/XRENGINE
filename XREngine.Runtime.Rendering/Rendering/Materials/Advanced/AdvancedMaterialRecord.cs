using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Fixed 64-byte GPU material header. Constants and texture references live in
/// separate packed arenas so rows sharing a kernel never require unique pipelines.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedMaterialRecord
{
    public uint StableRowId;
    public uint Generation;
    public uint ShadingKernelId;
    public uint ShadingKernelGeneration;

    public ulong MaterialLayoutHash;
    public EAdvancedMaterialRenderStateClass RenderStateClass;
    public EAdvancedMaterialCoverageMode CoverageMode;

    public EAdvancedMaterialRequiredAttributeMask RequiredAttributeMask;
    public uint TextureReferenceOffset;
    public uint TextureReferenceCount;
    public uint ConstantWordOffset;

    public uint ConstantWordCount;
    public EAdvancedMaterialFeatureFlags FeatureFlags;
    public EAdvancedMaterialEligibilityFlags EligibilityFlags;
    public uint Reserved;
}
