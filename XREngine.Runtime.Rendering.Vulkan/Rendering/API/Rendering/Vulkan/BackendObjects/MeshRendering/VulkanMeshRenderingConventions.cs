using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mesh-wrapper-local conventions that do not require a renderer facade.
/// </summary>
internal static class VulkanMeshRenderingConventions
{
    internal const uint DescriptorSetMaterial = 2;
    internal const ulong FrameSourceMutableDescriptorSignature = 0x4652534D55544453UL;
    internal const ShaderStageFlags CommonPushConstantStageFlags =
        ShaderStageFlags.VertexBit |
        ShaderStageFlags.TessellationControlBit |
        ShaderStageFlags.TessellationEvaluationBit |
        ShaderStageFlags.GeometryBit |
        ShaderStageFlags.FragmentBit |
        ShaderStageFlags.ComputeBit;

    /// <summary>
    /// Extends the fixed 16-byte common push-constant ABI only when the
    /// logical device enabled VK_EXT_mesh_shader.
    /// </summary>
    internal static ShaderStageFlags GetCommonPushConstantStageFlags(
        VulkanDeviceContext device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return CommonPushConstantStageFlags |
            (device.SupportsMeshTaskIndirectCount
                ? ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt
                : 0);
    }

    internal static bool CommandRecordingDiagnosticsEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanRecordingDiag);
    internal static bool CommandRecordingDetailProfilingEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanRecordingProfileDetail);
    internal static bool DescriptorTraceEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanDescriptorTrace);
    internal static bool BloomVulkanDiagnosticsEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.BloomDiag);

    internal static int SaturateToInt(ulong value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    internal static bool IsFrameSourceSamplerName(string? name)
        => string.Equals(name, "SourceTexture", StringComparison.Ordinal) ||
           string.Equals(name, "SourceTex", StringComparison.Ordinal) ||
           string.Equals(name, "SourceTexture0", StringComparison.Ordinal) ||
           string.Equals(name, "SourceTexture1", StringComparison.Ordinal);

    internal static bool IsMutableFrameSourceSamplerName(string? name, XRRenderPipelineInstance? pipeline)
        => IsFrameSourceSamplerName(name) ||
           (!string.IsNullOrWhiteSpace(name) && pipeline is not null &&
            pipeline.TryGetTexture(name, out XRTexture? texture) && texture is not null);

    internal static bool IsExpectedVulkanImageAllocationDeferral(Exception exception)
        => exception.Message.Contains("Vulkan image allocation deferred under", StringComparison.OrdinalIgnoreCase) ||
           exception.Message.Contains("allocation deferred under allocator pressure", StringComparison.OrdinalIgnoreCase);

    internal static void SetMaterialStaticUniforms(XRMaterial material, XRRenderProgram program)
    {
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMaterialParameterEmission(material.Parameters.Length);
        foreach (ShaderVar parameter in material.Parameters)
            parameter.SetUniform(program, forceUpdate: true);
    }

    internal static ECullMode ResolveCullMode(ECullMode mode)
        => !RuntimeEngine.Rendering.State.ReverseCulling ? mode : mode switch
        {
            ECullMode.Front => ECullMode.Back,
            ECullMode.Back => ECullMode.Front,
            _ => mode,
        };

    internal static EWinding ResolveWinding(EWinding winding)
        => !RuntimeEngine.Rendering.State.ReverseWinding ? winding :
            winding == EWinding.Clockwise ? EWinding.CounterClockwise : EWinding.Clockwise;

    internal static BlendMode? ResolveBlendMode(RenderingParameters parameters)
    {
        if (parameters.BlendModeAllDrawBuffers is not null)
            return parameters.BlendModeAllDrawBuffers;
        if (parameters.BlendModesPerDrawBuffer is null || parameters.BlendModesPerDrawBuffer.Count == 0)
            return null;
        return parameters.BlendModesPerDrawBuffer.TryGetValue(0u, out BlendMode? primary)
            ? primary
            : parameters.BlendModesPerDrawBuffer.Values.FirstOrDefault();
    }

    internal static ColorComponentFlags ToVulkanColorWriteMask(RenderingParameters parameters)
        => (parameters.WriteRed ? ColorComponentFlags.RBit : 0) |
           (parameters.WriteGreen ? ColorComponentFlags.GBit : 0) |
           (parameters.WriteBlue ? ColorComponentFlags.BBit : 0) |
           (parameters.WriteAlpha ? ColorComponentFlags.ABit : 0);

    internal static CullModeFlags ToVulkanCullMode(ECullMode mode) => mode switch
    {
        ECullMode.None => CullModeFlags.None,
        ECullMode.Back => CullModeFlags.BackBit,
        ECullMode.Front => CullModeFlags.FrontBit,
        ECullMode.Both => CullModeFlags.FrontAndBack,
        _ => CullModeFlags.BackBit,
    };

    internal static FrontFace ToVulkanFrontFace(EWinding winding)
        => winding == EWinding.Clockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise;

    internal static CompareOp ToVulkanCompareOp(EComparison comparison) => comparison switch
    {
        EComparison.Never => CompareOp.Never,
        EComparison.Less => CompareOp.Less,
        EComparison.Equal => CompareOp.Equal,
        EComparison.Lequal => CompareOp.LessOrEqual,
        EComparison.Greater => CompareOp.Greater,
        EComparison.Nequal => CompareOp.NotEqual,
        EComparison.Gequal => CompareOp.GreaterOrEqual,
        _ => CompareOp.Always,
    };

    internal static StencilOpState ToVulkanStencilState(StencilTestFace face) => new()
    {
        FailOp = ToVulkanStencilOp(face.BothFailOp), PassOp = ToVulkanStencilOp(face.BothPassOp),
        DepthFailOp = ToVulkanStencilOp(face.StencilPassDepthFailOp), CompareOp = ToVulkanCompareOp(face.Function),
        CompareMask = face.ReadMask, WriteMask = face.WriteMask, Reference = (uint)Math.Max(face.Reference, 0),
    };

    internal static BlendOp ToVulkanBlendOp(EBlendEquationMode mode) => mode switch
    {
        EBlendEquationMode.FuncSubtract => BlendOp.Subtract,
        EBlendEquationMode.FuncReverseSubtract => BlendOp.ReverseSubtract,
        EBlendEquationMode.Min => BlendOp.Min,
        EBlendEquationMode.Max => BlendOp.Max,
        _ => BlendOp.Add,
    };

    internal static BlendFactor ToVulkanBlendFactor(EBlendingFactor factor) => factor switch
    {
        EBlendingFactor.Zero => BlendFactor.Zero, EBlendingFactor.One => BlendFactor.One,
        EBlendingFactor.SrcColor => BlendFactor.SrcColor, EBlendingFactor.OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
        EBlendingFactor.DstColor => BlendFactor.DstColor, EBlendingFactor.OneMinusDstColor => BlendFactor.OneMinusDstColor,
        EBlendingFactor.SrcAlpha => BlendFactor.SrcAlpha, EBlendingFactor.OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
        EBlendingFactor.DstAlpha => BlendFactor.DstAlpha, EBlendingFactor.OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
        EBlendingFactor.SrcAlphaSaturate => BlendFactor.SrcAlphaSaturate,
        EBlendingFactor.ConstantColor => BlendFactor.ConstantColor, EBlendingFactor.OneMinusConstantColor => BlendFactor.OneMinusConstantColor,
        EBlendingFactor.ConstantAlpha => BlendFactor.ConstantAlpha, EBlendingFactor.OneMinusConstantAlpha => BlendFactor.OneMinusConstantAlpha,
        EBlendingFactor.Src1Color => BlendFactor.Src1Color, EBlendingFactor.OneMinusSrc1Color => BlendFactor.OneMinusSrc1Color,
        EBlendingFactor.Src1Alpha => BlendFactor.Src1Alpha, EBlendingFactor.OneMinusSrc1Alpha => BlendFactor.OneMinusSrc1Alpha,
        _ => BlendFactor.One,
    };

    private static StencilOp ToVulkanStencilOp(EStencilOp op) => op switch
    {
        EStencilOp.Zero => StencilOp.Zero, EStencilOp.Invert => StencilOp.Invert,
        EStencilOp.Replace => StencilOp.Replace, EStencilOp.Incr => StencilOp.IncrementAndClamp,
        EStencilOp.Decr => StencilOp.DecrementAndClamp, EStencilOp.IncrWrap => StencilOp.IncrementAndWrap,
        EStencilOp.DecrWrap => StencilOp.DecrementAndWrap, _ => StencilOp.Keep,
    };
}
