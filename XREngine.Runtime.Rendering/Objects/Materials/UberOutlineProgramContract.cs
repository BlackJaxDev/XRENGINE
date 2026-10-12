using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Cold-path admission for the exact verified canonical outline program and its complete reflected ABI.</summary>
public static class UberOutlineProgramContract
{
    public const uint MaterialByteSize = 592;
    public const int SourceMemberCount = 72;
    private static readonly ShaderAbiMemberContract[] Members =
    [
        new("OutlineFeatures_0", "OutlineFeatures", 0, 4, "u32"),
        new("_MainTex_ST_0", "_MainTex_ST", 16, 16, "vec4<f32>"),
        new("_MainTexPan_0", "_MainTexPan", 32, 8, "vec2<f32>"),
        new("_MainTexUV_0", "_MainTexUV", 40, 4, "i32"),
        new("_Color_0", "_Color", 48, 16, "vec4<f32>"),
        new("_MainVertexColoringEnabled_0", "_MainVertexColoringEnabled", 64, 4, "f32"),
        new("_MainVertexColoringLinearSpace_0", "_MainVertexColoringLinearSpace", 68, 4, "f32"),
        new("_MainVertexColoring_0", "_MainVertexColoring", 72, 4, "f32"),
        new("_MainUseVertexColorAlpha_0", "_MainUseVertexColorAlpha", 76, 4, "f32"),
        new("_AlphaMask_ST_0", "_AlphaMask_ST", 80, 16, "vec4<f32>"),
        new("_AlphaMaskPan_0", "_AlphaMaskPan", 96, 8, "vec2<f32>"),
        new("_AlphaMaskUV_0", "_AlphaMaskUV", 104, 4, "i32"),
        new("_MainAlphaMaskMode_0", "_MainAlphaMaskMode", 108, 4, "i32"),
        new("_AlphaMaskBlendStrength_0", "_AlphaMaskBlendStrength", 112, 4, "f32"),
        new("_AlphaMaskValue_0", "_AlphaMaskValue", 116, 4, "f32"),
        new("_AlphaMaskInvert_0", "_AlphaMaskInvert", 120, 4, "f32"),
        new("_Cutoff_0", "_Cutoff", 124, 4, "f32"),
        new("_Mode_0", "_Mode", 128, 4, "i32"),
        new("_AlphaForceOpaque_0", "_AlphaForceOpaque", 132, 4, "f32"),
        new("_AlphaMod_0", "_AlphaMod", 136, 4, "f32"),
        new("_OutlineColor_0", "_OutlineColor", 144, 16, "vec4<f32>"),
        new("_OutlineWidth_0", "_OutlineWidth", 160, 4, "f32"),
        new("_OutlineEmission_0", "_OutlineEmission", 164, 4, "f32"),
        new("_OutlineLit_0", "_OutlineLit", 168, 4, "f32"),
        new("_OutlineDistanceFadeStart_0", "_OutlineDistanceFadeStart", 172, 4, "f32"),
        new("_OutlineDistanceFadeEnd_0", "_OutlineDistanceFadeEnd", 176, 4, "f32"),
        new("_OutlineTextureTint_0", "_OutlineTextureTint", 180, 4, "f32"),
        new("_OutlineVertexColorTint_0", "_OutlineVertexColorTint", 184, 4, "f32"),
        new("_OutlineExpansionMode_0", "_OutlineExpansionMode", 188, 4, "i32"),
        new("_OutlineSpace_0", "_OutlineSpace", 192, 4, "i32"),
        new("_OutlinePersonaDirection_0", "_OutlinePersonaDirection", 208, 12, "vec3<f32>"),
        new("_OutlineDropShadowOffset_0", "_OutlineDropShadowOffset", 224, 12, "vec3<f32>"),
        new("_OutlineFixedSize_0", "_OutlineFixedSize", 236, 4, "f32"),
        new("_OutlineUseVertexColors_0", "_OutlineUseVertexColors", 240, 4, "f32"),
        new("_OutlineZOffset_0", "_OutlineZOffset", 244, 4, "f32"),
        new("_OutlineTexture_ST_0", "_OutlineTexture_ST", 256, 16, "vec4<f32>"),
        new("_OutlineTexturePan_0", "_OutlineTexturePan", 272, 8, "vec2<f32>"),
        new("_OutlineTextureUV_0", "_OutlineTextureUV", 280, 4, "i32"),
        new("_OutlineMask_ST_0", "_OutlineMask_ST", 288, 16, "vec4<f32>"),
        new("_OutlineMaskPan_0", "_OutlineMaskPan", 304, 8, "vec2<f32>"),
        new("_OutlineMaskUV_0", "_OutlineMaskUV", 312, 4, "i32"),
        new("_OutlineHueShift_0", "_OutlineHueShift", 316, 4, "f32"),
        new("_OutlineHueShiftSpeed_0", "_OutlineHueShiftSpeed", 320, 4, "f32"),
        new("_OutlineShadowStrength_0", "_OutlineShadowStrength", 324, 4, "f32"),
        new("_DissolveType_0", "_DissolveType", 328, 4, "f32"),
        new("_DissolveProgress_0", "_DissolveProgress", 332, 4, "f32"),
        new("_DissolveEdgeColor_0", "_DissolveEdgeColor", 336, 16, "vec4<f32>"),
        new("_DissolveEdgeWidth_0", "_DissolveEdgeWidth", 352, 4, "f32"),
        new("_DissolveEdgeEmission_0", "_DissolveEdgeEmission", 356, 4, "f32"),
        new("_DissolveNoiseTexture_ST_0", "_DissolveNoiseTexture_ST", 368, 16, "vec4<f32>"),
        new("_DissolveNoiseStrength_0", "_DissolveNoiseStrength", 384, 4, "f32"),
        new("_DissolveStartPoint_0", "_DissolveStartPoint", 400, 12, "vec3<f32>"),
        new("_DissolveEndPoint_0", "_DissolveEndPoint", 416, 12, "vec3<f32>"),
        new("_DissolveInvert_0", "_DissolveInvert", 428, 4, "f32"),
        new("_DissolveNoiseTexturePan_0", "_DissolveNoiseTexturePan", 432, 8, "vec2<f32>"),
        new("_DissolveNoiseTextureUV_0", "_DissolveNoiseTextureUV", 440, 4, "i32"),
        new("_DissolveDetailNoise_ST_0", "_DissolveDetailNoise_ST", 448, 16, "vec4<f32>"),
        new("_DissolveDetailNoisePan_0", "_DissolveDetailNoisePan", 464, 8, "vec2<f32>"),
        new("_DissolveDetailNoiseUV_0", "_DissolveDetailNoiseUV", 472, 4, "i32"),
        new("_DissolveDetailStrength_0", "_DissolveDetailStrength", 476, 4, "f32"),
        new("_DissolveMask_ST_0", "_DissolveMask_ST", 480, 16, "vec4<f32>"),
        new("_DissolveMaskPan_0", "_DissolveMaskPan", 496, 8, "vec2<f32>"),
        new("_DissolveMaskUV_0", "_DissolveMaskUV", 504, 4, "i32"),
        new("_DissolveMaskInvert_0", "_DissolveMaskInvert", 508, 4, "f32"),
        new("_DissolveContinuous_0", "_DissolveContinuous", 512, 4, "f32"),
        new("_DissolveCoordinateSpace_0", "_DissolveCoordinateSpace", 516, 4, "i32"),
        new("_DissolveTileGrid_0", "_DissolveTileGrid", 520, 8, "vec2<f32>"),
        new("_DissolveHueShift_0", "_DissolveHueShift", 528, 4, "f32"),
        new("_DissolveEdgeGradient_ST_0", "_DissolveEdgeGradient_ST", 544, 16, "vec4<f32>"),
        new("_DissolveEdgeTexture_ST_0", "_DissolveEdgeTexture_ST", 560, 16, "vec4<f32>"),
        new("_DissolveEdgeTexturePan_0", "_DissolveEdgeTexturePan", 576, 8, "vec2<f32>"),
        new("_DissolveEdgeTextureUV_0", "_DissolveEdgeTextureUV", 584, 4, "i32"),
        new("_DissolveCutoff_0", "_DissolveCutoff", 588, 4, "f32"),
    ];
    private static readonly ShaderAbiMemberContract[] ViewMembers =
    [
        new("viewProjection_0", "ViewProjection", 0, 64, "mat4x4<f32>", MatrixOrder: ShaderAbiMatrixOrder.ColumnMajor, MatrixStride: 16),
        new("cameraPosition_0", "CameraPosition", 64, 16, "vec4<f32>"),
        new("u_ScreenParams_0", "u_ScreenParams", 80, 16, "vec4<f32>"),
        new("u_Time_0", "u_Time", 96, 4, "f32"),
    ];
    private static readonly ShaderAbiMemberContract[] ObjectMembers =
    [
        new("modelMatrix_0", "ModelMatrix", 0, 64, "mat4x4<f32>", MatrixOrder: ShaderAbiMatrixOrder.ColumnMajor, MatrixStride: 16),
        new("normalMatrix_0", "NormalMatrix", 64, 64, "mat4x4<f32>", MatrixOrder: ShaderAbiMatrixOrder.ColumnMajor, MatrixStride: 16),
    ];
    private static readonly ShaderVertexAttribute[] VertexAttributes =
    [
        new(0, 0, "float32x3", "position"), new(1, 12, "float32x3", "normal"),
        new(2, 24, "float32x2", "uv0"), new(3, 32, "float32x2", "uv1"),
        new(4, 40, "float32x2", "uv2"), new(5, 48, "float32x2", "uv3"),
        new(6, 56, "float32x4", "color0"),
    ];

    /// <summary>The exact source-owned numeric fields, excluding the engine-produced feature word.</summary>
    public static ReadOnlySpan<ShaderAbiMemberContract> SourceMembers => Members.AsSpan(1);

    /// <summary>Returns whether a source field is required by the selected authored coverage closure.</summary>
    public static bool IsSourceMemberActive(int sourceIndex, uint features)
    {
        int memberIndex = sourceIndex + 1;
        return memberIndex is >= 1 and <= 72 &&
            (memberIndex is < 9 or > 15 || (features & UberOutlineMaterialProfile.AlphaMasks) != 0) &&
            (memberIndex < 44 || (features & UberOutlineMaterialProfile.Dissolve) != 0);
    }

    public static EngineMaterialVariantKey Key(uint features)
    {
        string output = features switch
        {
            0 => "linear-hdr-v1",
            1 => "linear-hdr-alpha-mask-v1",
            2 => "linear-hdr-dissolve-v1",
            3 => "linear-hdr-alpha-mask-dissolve-v1",
            _ => throw new ArgumentOutOfRangeException(nameof(features)),
        };
        return new(EngineMaterialSemanticIdentity.UberOutlineV1, ShaderCompileTarget.WebGPUWgsl,
            "outline", "position-normal-uv4-color-v1", output);
    }

    /// <summary>Validates once at program installation; descriptor parsing and asset access never belong in draw validation.</summary>
    public static void Validate(ShaderProgramArtifact artifact, uint features)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        EngineMaterialVariantKey key = Key(features);
        string name = features switch
        {
            0 => "engine-uber-outline", 1 => "engine-uber-outline-alpha-mask",
            2 => "engine-uber-outline-dissolve", _ => "engine-uber-outline-alpha-mask-dissolve",
        };
        int textures = 3 + ((features & 1) != 0 ? 1 : 0) + ((features & 2) != 0 ? 5 : 0);
        if (artifact.DescriptorBytes.IsDefaultOrEmpty || artifact.Target != key.Target ||
            artifact.SourceLanguage != "Slang" || artifact.Name != name || artifact.Pass != key.Pass ||
            artifact.SemanticSchemaIdentity != "xrengine.engine.uber-outline.v1" ||
            artifact.Coordinates != "xrengine.webgpu.coordinates.v1" ||
            artifact.VertexEntryPoint != "uberOutlineVertex" || artifact.FragmentEntryPoint != "uberOutlineFragment" ||
            artifact.ComputeEntryPoint is not null || artifact.ComputeWorkgroupSize is not null ||
            artifact.Resources.Length != 3 + textures * 2 || artifact.VertexBuffers.Length != 1)
            throw Invalid();
        using (JsonDocument descriptor = JsonDocument.Parse(artifact.DescriptorBytes.ToArray()))
        {
            if (!descriptor.RootElement.TryGetProperty("materialVariant", out JsonElement variant) ||
                ShaderProgramArtifactReader.ReadMaterialVariantKey(variant, artifact.Pass, artifact.Target) != key)
                throw Invalid();
            if (!descriptor.RootElement.TryGetProperty("defines", out JsonElement defines) || defines.ValueKind != JsonValueKind.Array ||
                !descriptor.RootElement.TryGetProperty("includes", out JsonElement includes) || includes.ValueKind != JsonValueKind.Array ||
                !descriptor.RootElement.TryGetProperty("requiredFeatures", out JsonElement requiredFeatures) || requiredFeatures.ValueKind != JsonValueKind.Array)
                throw Invalid();
            int expectedDefines = ((features & 1) != 0 ? 1 : 0) + ((features & 2) != 0 ? 1 : 0);
            if (defines.GetArrayLength() != expectedDefines ||
                includes.GetArrayLength() != 0 || requiredFeatures.GetArrayLength() != 0)
                throw Invalid();
            int defineIndex = 0;
            if ((features & 1) != 0 && defines[defineIndex++].GetString() != "XRE_OUTLINE_ALPHA_MASKS") throw Invalid();
            if ((features & 2) != 0 && defines[defineIndex].GetString() != "XRE_OUTLINE_DISSOLVE") throw Invalid();
        }

        ShaderVertexBufferLayout vertex = artifact.VertexBuffers[0];
        if (vertex.Slot != 0 || vertex.Stride != 72 || vertex.StepMode != "vertex" || vertex.Attributes.Length != VertexAttributes.Length)
            throw Invalid();
        for (int index = 0; index < VertexAttributes.Length; index++)
            if (vertex.Attributes[index] != VertexAttributes[index]) throw Invalid();
        Uniform(artifact, 0, 0, "View", "view_0", ShaderAbiResourceOwner.Engine, ShaderAbiFrequency.View,
            ShaderStageVisibility.Vertex | ShaderStageVisibility.Fragment, 112, ViewMembers);
        Uniform(artifact, 0, 1, "Object", "object_0", ShaderAbiResourceOwner.Engine, ShaderAbiFrequency.Object,
            ShaderStageVisibility.Vertex, 128, ObjectMembers);
        Uniform(artifact, 1, 0, "UberOutlineMaterial", "material_0", ShaderAbiResourceOwner.Material, ShaderAbiFrequency.Material,
            ShaderStageVisibility.Vertex | ShaderStageVisibility.Fragment, MaterialByteSize, Members);
        for (int index = 0; index < textures; index++)
        {
            string samplerName = UberOutlineMaterialProfile.RequiredSamplerName(features, index);
            uint binding = checked((uint)(index < 3 || (features & 1) != 0 && index == 3 ? index * 2 :
                8 + (index - 3 - ((features & 1) != 0 ? 1 : 0)) * 2));
            Sampled(artifact, binding, samplerName, false);
            Sampled(artifact, binding + 1, samplerName, true);
        }
        Limit(artifact, "maxVertexAttributes", 7);
        Limit(artifact, "maxVertexBuffers", 7);
        Limit(artifact, "maxVertexBufferArrayStride", 72);
        Limit(artifact, "maxBindGroups", 3);
        Limit(artifact, "maxBindingsPerBindGroup", textures * 2);
        Limit(artifact, "maxUniformBufferBindingSize", checked((int)MaterialByteSize));
        Limit(artifact, "maxDynamicUniformBuffersPerPipelineLayout", 3);
        Limit(artifact, "maxUniformBuffersPerShaderStage", 3);
        Limit(artifact, "maxSampledTexturesPerShaderStage", textures);
        Limit(artifact, "maxSamplersPerShaderStage", textures);
        if (artifact.RequiredLimits.Count != 10) throw Invalid();
    }

    private static void Uniform(ShaderProgramArtifact artifact, uint group, uint binding, string name, string physicalName,
        ShaderAbiResourceOwner owner, ShaderAbiFrequency frequency, ShaderStageVisibility visibility,
        uint bytes, ReadOnlySpan<ShaderAbiMemberContract> members)
    {
        ShaderStageResourceLayout resource = Binding(artifact, group, binding);
        ShaderAbiResourceContract contract = resource.Contract;
        if (resource.BindingType != "uniform" || !resource.DynamicOffset || resource.RuntimeArray || resource.Visibility != visibility ||
            contract.Name != name || contract.PhysicalName != physicalName || contract.Kind != ShaderAbiResourceKind.UniformBuffer ||
            contract.Owner != owner || contract.Frequency != frequency || contract.ByteSize != bytes ||
            contract.DescriptorLifetime is not null || contract.Members.Length != members.Length)
            throw Invalid();
        for (int index = 0; index < members.Length; index++)
            if (contract.Members[index] != members[index]) throw Invalid();
    }

    private static void Sampled(ShaderProgramArtifact artifact, uint binding, string name, bool sampler)
    {
        ShaderStageResourceLayout resource = Binding(artifact, 2, binding);
        ShaderAbiResourceContract contract = resource.Contract;
        if (resource.BindingType != (sampler ? "filtering-sampler" : "texture-2d-float") ||
            resource.DynamicOffset || resource.RuntimeArray || resource.Visibility != ShaderStageVisibility.Fragment ||
            contract.Name != name || contract.PhysicalName != (sampler ? name + "Sampler_0" : name + "_0") ||
            contract.Kind != (sampler ? ShaderAbiResourceKind.Sampler : ShaderAbiResourceKind.SampledImage) ||
            contract.Owner != ShaderAbiResourceOwner.Material || contract.Frequency != ShaderAbiFrequency.Material ||
            contract.ByteSize != 0 || !contract.Members.IsEmpty || contract.DescriptorLifetime is not null)
            throw Invalid();
    }

    private static ShaderStageResourceLayout Binding(ShaderProgramArtifact artifact, uint group, uint binding)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Set == group && resource.Contract.Binding == binding) return resource;
        throw Invalid();
    }

    private static void Limit(ShaderProgramArtifact artifact, string name, int expected)
    {
        if (!artifact.RequiredLimits.TryGetValue(name, out int actual) || actual != expected) throw Invalid();
    }

    private static NotSupportedException Invalid()
        => new("UberOutline.ProgramAbiMismatch: the verified artifact must implement the complete canonical outline key, stages, vertex inputs, resources, and numeric ABI.");
}
