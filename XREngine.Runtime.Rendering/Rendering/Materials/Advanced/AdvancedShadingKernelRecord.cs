using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Pipeline identity shared by any number of material instance rows.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedShadingKernelRecord
{
    public uint StableKernelId;
    public uint Generation;
    public ulong MaterialLayoutHash;

    public EAdvancedMaterialRequiredAttributeMask RequiredAttributeMask;
    public uint SupportedCoverageMask;
    public EAdvancedMaterialEligibilityFlags SupportedEligibility;
    public EAdvancedMaterialFeatureFlags SupportedFeatures;

    public ulong ShaderIdentityHash;
    public uint RenderStateClassMask;
    public uint Flags;
    public uint Reserved0;

    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
}
