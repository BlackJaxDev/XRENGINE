using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// Checks managed and interop sizes for shader-visible advanced records before
/// shader compilation. Compiled shader offsets and strides are checked by the
/// editor SPIR-V validator during shader packaging.
/// </summary>
public static class AdvancedShaderRecordLayout
{
    public static void ValidateCpuLayouts()
    {
        RequireMatchingSize<AdvancedGpuHandle>();
        RequireMatchingSize<AdvancedGpuHandleLookup>();
        RequireMatchingSize<AdvancedGpuHandleRemap>();
        RequireMatchingSize<AdvancedBufferReference>();
        RequireMatchingSize<AdvancedDrawRecord>();
        RequireMatchingSize<AdvancedInstanceRecord>();
        RequireMatchingSize<AdvancedGeometryRecord>();
        RequireMatchingSize<AdvancedTransformRecord>();
        RequireMatchingSize<AdvancedDeformationRecord>();
        RequireMatchingSize<AdvancedRenderStateRecord>();
        RequireMatchingSize<AdvancedEditorIdentityRecord>();
        RequireMatchingSize<AdvancedMaterialRecord>();
        RequireMatchingSize<AdvancedShadingKernelRecord>();
        RequireMatchingSize<AdvancedMaterialLayoutRecord>();
        RequireMatchingSize<AdvancedMaterialLayoutMember>();
        RequireMatchingSize<AdvancedMaterialTextureBinding>();
        RequireMatchingSize<AdvancedViewRecord>();
        RequireMatchingSize<AdvancedLightRecord>();
        RequireMatchingSize<AdvancedShadowRecord>();
        RequireMatchingSize<AdvancedProbeRecord>();
        RequireMatchingSize<AdvancedEnvironmentRecord>();
        RequireMatchingSize<AdvancedDecalRecord>();
        RequireMatchingSize<AdvancedGiResourceRecord>();
        RequireMatchingSize<AdvancedTextureRecord>();
        RequireMatchingSize<AdvancedSamplerRecord>();
        RequireMatchingSize<AdvancedEncodedTextureReference>();
        RequireMatchingSize<AdvancedEncodedSamplerReference>();
    }

    private static void RequireMatchingSize<T>() where T : unmanaged
    {
        int unsafeSize = Unsafe.SizeOf<T>();
        int marshalSize = Marshal.SizeOf<T>();
        if (unsafeSize != marshalSize)
            throw new InvalidOperationException($"{typeof(T).Name} CPU size mismatch: Unsafe.SizeOf={unsafeSize}, Marshal.SizeOf={marshalSize}.");
    }
}
