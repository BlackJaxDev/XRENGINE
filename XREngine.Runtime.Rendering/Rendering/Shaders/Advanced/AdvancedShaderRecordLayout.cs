using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// CPU authority for every shader-visible advanced record size and selected
/// byte offsets. The generated defines are checked by <c>AdvancedLayout.glslinc</c>.
/// </summary>
public static class AdvancedShaderRecordLayout
{
    public const int GpuHandleSize = 8;
    public const int GpuHandleLookupSize = 8;
    public const int BufferReferenceSize = 32;
    public const int DrawSize = 80;
    public const int InstanceSize = 224;
    public const int GeometrySize = 256;
    public const int TransformSize = 80;
    public const int DeformationSize = 48;
    public const int RenderStateSize = 32;
    public const int EditorIdentitySize = 32;
    public const int MaterialSize = 64;
    public const int ShadingKernelSize = 64;
    public const int MaterialLayoutSize = 48;
    public const int MaterialLayoutMemberSize = 32;
    public const int MaterialTextureBindingSize = 32;
    public const int ViewSize = 896;
    public const int LightSize = 128;
    public const int ShadowSize = 224;
    public const int ProbeSize = 176;
    public const int EnvironmentSize = 128;
    public const int DecalSize = 192;
    public const int GiResourceSize = 208;
    public const int TextureSize = 64;
    public const int SamplerSize = 64;
    public const int EncodedTextureReferenceSize = 16;
    public const int EncodedSamplerReferenceSize = 16;

    public static void ValidateCpuLayouts()
    {
        RequireSize<AdvancedGpuHandle>(GpuHandleSize);
        RequireSize<AdvancedGpuHandleLookup>(GpuHandleLookupSize);
        RequireSize<AdvancedBufferReference>(BufferReferenceSize);
        RequireSize<AdvancedDrawRecord>(DrawSize);
        RequireSize<AdvancedInstanceRecord>(InstanceSize);
        RequireSize<AdvancedGeometryRecord>(GeometrySize);
        RequireSize<AdvancedTransformRecord>(TransformSize);
        RequireSize<AdvancedDeformationRecord>(DeformationSize);
        RequireSize<AdvancedRenderStateRecord>(RenderStateSize);
        RequireSize<AdvancedEditorIdentityRecord>(EditorIdentitySize);
        RequireSize<AdvancedMaterialRecord>(MaterialSize);
        RequireSize<AdvancedShadingKernelRecord>(ShadingKernelSize);
        RequireSize<AdvancedMaterialLayoutRecord>(MaterialLayoutSize);
        RequireSize<AdvancedMaterialLayoutMember>(MaterialLayoutMemberSize);
        RequireSize<AdvancedMaterialTextureBinding>(MaterialTextureBindingSize);
        RequireSize<AdvancedViewRecord>(ViewSize);
        RequireSize<AdvancedLightRecord>(LightSize);
        RequireSize<AdvancedShadowRecord>(ShadowSize);
        RequireSize<AdvancedProbeRecord>(ProbeSize);
        RequireSize<AdvancedEnvironmentRecord>(EnvironmentSize);
        RequireSize<AdvancedDecalRecord>(DecalSize);
        RequireSize<AdvancedGiResourceRecord>(GiResourceSize);
        RequireSize<AdvancedTextureRecord>(TextureSize);
        RequireSize<AdvancedSamplerRecord>(SamplerSize);
        RequireSize<AdvancedEncodedTextureReference>(EncodedTextureReferenceSize);
        RequireSize<AdvancedEncodedSamplerReference>(EncodedSamplerReferenceSize);

        RequireOffset<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.Material), 16);
        RequireOffset<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.CurrentTransform), 48);
        RequireOffset<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.PreviousWorld), 64);
        RequireOffset<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.BoundsSphere), 128);
        RequireOffset<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.Animation), 176);
        RequireOffset<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.VertexLayoutId), 160);
        RequireOffset<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.BoundsSphere), 176);
        RequireOffset<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.MaterialSectionFirst), 224);
        RequireOffset<AdvancedTransformRecord>(nameof(AdvancedTransformRecord.FrameSlot), 64);
        RequireOffset<AdvancedDeformationRecord>(nameof(AdvancedDeformationRecord.CurrentGeometry), 8);
        RequireOffset<AdvancedMaterialRecord>(nameof(AdvancedMaterialRecord.MaterialLayoutHash), 16);
        RequireOffset<AdvancedMaterialRecord>(nameof(AdvancedMaterialRecord.TextureReferenceOffset), 36);
        RequireOffset<AdvancedShadingKernelRecord>(nameof(AdvancedShadingKernelRecord.ShaderIdentityHash), 32);
        RequireOffset<AdvancedMaterialLayoutRecord>(nameof(AdvancedMaterialLayoutRecord.MemberOffset), 16);
        RequireOffset<AdvancedViewRecord>(nameof(AdvancedViewRecord.PreviousView), 320);
        RequireOffset<AdvancedViewRecord>(nameof(AdvancedViewRecord.InverseViewProjectionJittered), 640);
        RequireOffset<AdvancedViewRecord>(nameof(AdvancedViewRecord.CameraPositionAndNear), 768);
        RequireOffset<AdvancedViewRecord>(nameof(AdvancedViewRecord.ViewId), 864);
        RequireOffset<AdvancedLightRecord>(nameof(AdvancedLightRecord.CookieTexture), 80);
        RequireOffset<AdvancedLightRecord>(nameof(AdvancedLightRecord.ShadowRecord), 96);
        RequireOffset<AdvancedShadowRecord>(nameof(AdvancedShadowRecord.WorldToShadow), 32);
        RequireOffset<AdvancedProbeRecord>(nameof(AdvancedProbeRecord.Irradiance), 128);
        RequireOffset<AdvancedEnvironmentRecord>(nameof(AdvancedEnvironmentRecord.RotationAndExposure), 80);
        RequireOffset<AdvancedDecalRecord>(nameof(AdvancedDecalRecord.WorldToDecal), 32);
        RequireOffset<AdvancedDecalRecord>(nameof(AdvancedDecalRecord.MaskTexture), 176);
        RequireOffset<AdvancedGiResourceRecord>(nameof(AdvancedGiResourceRecord.WorldToGrid), 64);
        RequireOffset<AdvancedGiResourceRecord>(nameof(AdvancedGiResourceRecord.BufferResourceOffset), 192);
        RequireOffset<AdvancedTextureRecord>(nameof(AdvancedTextureRecord.UvScaleBias), 48);
        RequireOffset<AdvancedSamplerRecord>(nameof(AdvancedSamplerRecord.LodBiasMinMaxAnisotropy), 32);
    }

    /// <summary>
    /// Emits integer preprocessor values consumed by shader-side static checks.
    /// </summary>
    public static string BuildCpuLayoutDefines()
    {
        ValidateCpuLayouts();
        StringBuilder source = new(2048);
        AppendDefine(source, "XR_ADV_CPU_SIZE_HANDLE", GpuHandleSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_HANDLE_LOOKUP", GpuHandleLookupSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_BUFFER_REFERENCE", BufferReferenceSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_DRAW", DrawSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_INSTANCE", InstanceSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_GEOMETRY", GeometrySize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_TRANSFORM", TransformSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_DEFORMATION", DeformationSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_RENDER_STATE", RenderStateSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_EDITOR_IDENTITY", EditorIdentitySize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_MATERIAL", MaterialSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_SHADING_KERNEL", ShadingKernelSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_MATERIAL_LAYOUT", MaterialLayoutSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_MATERIAL_LAYOUT_MEMBER", MaterialLayoutMemberSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_MATERIAL_TEXTURE_BINDING", MaterialTextureBindingSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_VIEW", ViewSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_LIGHT", LightSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_SHADOW", ShadowSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_PROBE", ProbeSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_ENVIRONMENT", EnvironmentSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_DECAL", DecalSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_GI_RESOURCE", GiResourceSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_TEXTURE", TextureSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_SAMPLER", SamplerSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_ENCODED_TEXTURE", EncodedTextureReferenceSize);
        AppendDefine(source, "XR_ADV_CPU_SIZE_ENCODED_SAMPLER", EncodedSamplerReferenceSize);

        AppendDefine(source, "XR_ADV_CPU_OFFSET_BUFFER_REFERENCE_BYTE_OFFSET", 8);
        AppendOffset<AdvancedDrawRecord>(source, "DRAW_MATERIAL", nameof(AdvancedDrawRecord.Material));
        AppendOffset<AdvancedDrawRecord>(source, "DRAW_CURRENT_TRANSFORM", nameof(AdvancedDrawRecord.CurrentTransform));
        AppendOffset<AdvancedInstanceRecord>(source, "INSTANCE_PREVIOUS_WORLD", nameof(AdvancedInstanceRecord.PreviousWorld));
        AppendOffset<AdvancedInstanceRecord>(source, "INSTANCE_BOUNDS_SPHERE", nameof(AdvancedInstanceRecord.BoundsSphere));
        AppendOffset<AdvancedInstanceRecord>(source, "INSTANCE_ANIMATION", nameof(AdvancedInstanceRecord.Animation));
        AppendOffset<AdvancedGeometryRecord>(source, "GEOMETRY_VERTEX_LAYOUT", nameof(AdvancedGeometryRecord.VertexLayoutId));
        AppendOffset<AdvancedGeometryRecord>(source, "GEOMETRY_BOUNDS_SPHERE", nameof(AdvancedGeometryRecord.BoundsSphere));
        AppendOffset<AdvancedGeometryRecord>(source, "GEOMETRY_MATERIAL_SECTION", nameof(AdvancedGeometryRecord.MaterialSectionFirst));
        AppendOffset<AdvancedTransformRecord>(source, "TRANSFORM_FRAME_SLOT", nameof(AdvancedTransformRecord.FrameSlot));
        AppendOffset<AdvancedDeformationRecord>(source, "DEFORMATION_CURRENT_GEOMETRY", nameof(AdvancedDeformationRecord.CurrentGeometry));
        AppendOffset<AdvancedMaterialRecord>(source, "MATERIAL_LAYOUT_HASH", nameof(AdvancedMaterialRecord.MaterialLayoutHash));
        AppendOffset<AdvancedMaterialRecord>(source, "MATERIAL_TEXTURE_OFFSET", nameof(AdvancedMaterialRecord.TextureReferenceOffset));
        AppendOffset<AdvancedShadingKernelRecord>(source, "KERNEL_SHADER_HASH", nameof(AdvancedShadingKernelRecord.ShaderIdentityHash));
        AppendOffset<AdvancedMaterialLayoutRecord>(source, "LAYOUT_MEMBER_OFFSET", nameof(AdvancedMaterialLayoutRecord.MemberOffset));
        AppendOffset<AdvancedViewRecord>(source, "VIEW_PREVIOUS_VIEW", nameof(AdvancedViewRecord.PreviousView));
        AppendOffset<AdvancedViewRecord>(source, "VIEW_INVERSE_VIEW_PROJECTION", nameof(AdvancedViewRecord.InverseViewProjectionJittered));
        AppendOffset<AdvancedViewRecord>(source, "VIEW_CAMERA_POSITION", nameof(AdvancedViewRecord.CameraPositionAndNear));
        AppendOffset<AdvancedViewRecord>(source, "VIEW_ID", nameof(AdvancedViewRecord.ViewId));
        AppendOffset<AdvancedLightRecord>(source, "LIGHT_COOKIE", nameof(AdvancedLightRecord.CookieTexture));
        AppendOffset<AdvancedLightRecord>(source, "LIGHT_SHADOW", nameof(AdvancedLightRecord.ShadowRecord));
        AppendOffset<AdvancedShadowRecord>(source, "SHADOW_WORLD_TO_SHADOW", nameof(AdvancedShadowRecord.WorldToShadow));
        AppendOffset<AdvancedProbeRecord>(source, "PROBE_IRRADIANCE", nameof(AdvancedProbeRecord.Irradiance));
        AppendOffset<AdvancedEnvironmentRecord>(source, "ENVIRONMENT_ROTATION", nameof(AdvancedEnvironmentRecord.RotationAndExposure));
        AppendOffset<AdvancedDecalRecord>(source, "DECAL_WORLD_TO_DECAL", nameof(AdvancedDecalRecord.WorldToDecal));
        AppendOffset<AdvancedDecalRecord>(source, "DECAL_MASK_TEXTURE", nameof(AdvancedDecalRecord.MaskTexture));
        AppendOffset<AdvancedGiResourceRecord>(source, "GI_WORLD_TO_GRID", nameof(AdvancedGiResourceRecord.WorldToGrid));
        AppendOffset<AdvancedGiResourceRecord>(source, "GI_BUFFER_OFFSET", nameof(AdvancedGiResourceRecord.BufferResourceOffset));
        AppendOffset<AdvancedTextureRecord>(source, "TEXTURE_UV_SCALE_BIAS", nameof(AdvancedTextureRecord.UvScaleBias));
        AppendOffset<AdvancedSamplerRecord>(source, "SAMPLER_LOD_PARAMS", nameof(AdvancedSamplerRecord.LodBiasMinMaxAnisotropy));
        return source.ToString();
    }

    private static void RequireSize<T>(int expected) where T : struct
    {
        int actual = Marshal.SizeOf<T>();
        if (actual != expected)
            throw new InvalidOperationException($"{typeof(T).Name} is {actual} bytes; shader layout requires {expected}.");
    }

    private static void RequireOffset<T>(string fieldName, int expected) where T : struct
    {
        int actual = checked((int)Marshal.OffsetOf<T>(fieldName));
        if (actual != expected)
            throw new InvalidOperationException($"{typeof(T).Name}.{fieldName} is at byte {actual}; shader layout requires {expected}.");
    }

    private static void AppendOffset<T>(
        StringBuilder source,
        string defineSuffix,
        string fieldName) where T : struct
        => AppendDefine(
            source,
            $"XR_ADV_CPU_OFFSET_{defineSuffix}",
            checked((int)Marshal.OffsetOf<T>(fieldName)));

    private static void AppendDefine(StringBuilder source, string name, int value)
        => source
            .Append("#define ")
            .Append(name)
            .Append(' ')
            .Append(value.ToString(CultureInfo.InvariantCulture))
            .AppendLine();
}
