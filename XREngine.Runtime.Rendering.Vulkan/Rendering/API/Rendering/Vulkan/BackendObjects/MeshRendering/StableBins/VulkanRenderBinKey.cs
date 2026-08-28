using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Numeric identity of a stable submission bin. It deliberately excludes all
/// data-only state (transforms, visibility, material values, and camera data).
/// </summary>
internal readonly record struct VulkanRenderBinKey(
    ulong PassCompatibility,
    ulong PipelineVariant,
    ulong GeometryPage,
    uint TopologyAndIndexType,
    ulong DescriptorModel,
    uint ViewMask,
    VulkanRenderBinOrderingClass OrderingClass,
    VulkanRenderBinNativeCompatibility NativeCompatibility,
    VulkanRenderBinContextCompatibility ContextCompatibility)
{
    internal bool IsValid => PassCompatibility != 0u &&
        PipelineVariant != 0u && GeometryPage != 0u &&
        DescriptorModel != 0u;

    internal static VulkanRenderBinKey Create(
        in VulkanResidentDrawTemplateStructuralIdentity structuralIdentity,
        in VulkanResidentDrawTemplateVariantKey variant,
        in VulkanResidentDrawTemplateNativeState nativeState)
        => Create(
            structuralIdentity,
            variant,
            nativeState,
            viewMask: variant.OutputProfileVariant == 0u ? 1u : 0u,
            orderingClass: VulkanRenderBinOrderingClass.Opaque,
            contextCompatibility: default);

    /// <summary>
    /// Builds the execution key after output contexts have been coalesced. The
    /// retained template intentionally contributes only structural fields; view
    /// mask and ordering are current-frame execution truth, never guesses made
    /// while a template was published.
    /// </summary>
    internal static VulkanRenderBinKey CreateForContext(
        in VulkanResidentDrawTemplateStructuralIdentity structuralIdentity,
        in VulkanResidentDrawTemplateVariantKey variant,
        in VulkanResidentDrawTemplateNativeState nativeState,
        in FrameOpContext context,
        bool isDynamicUi,
        bool preserveSubmissionOrder)
    {
        VulkanRenderBinOrderingClass ordering = isDynamicUi
            ? VulkanRenderBinOrderingClass.Ui
            : preserveSubmissionOrder || context.PreserveSubmissionOrderBlock
                ? VulkanRenderBinOrderingClass.Callback
                : nativeState.DrawTemplate.BlendEnabled
                    ? VulkanRenderBinOrderingClass.Transparent
                    : VulkanRenderBinOrderingClass.Opaque;
        uint viewMask = context.OutputSchedulingRequest.IsDefined
            ? context.OutputSchedulingRequest.Target.ViewMask
            : 0u;
        if (viewMask == 0u)
        {
            viewMask = context.MultiviewEnabled || context.StereoEnabled
                ? 0b11u
                : 0b1u;
        }
        return Create(
            structuralIdentity,
            variant,
            nativeState,
            viewMask,
            ordering,
            VulkanRenderBinContextCompatibility.Create(in context));
    }

    private static VulkanRenderBinKey Create(
        in VulkanResidentDrawTemplateStructuralIdentity structuralIdentity,
        in VulkanResidentDrawTemplateVariantKey variant,
        in VulkanResidentDrawTemplateNativeState nativeState,
        uint viewMask,
        VulkanRenderBinOrderingClass orderingClass,
        in VulkanRenderBinContextCompatibility contextCompatibility)
    {
        VulkanPreparedMeshPrimitive primitive = nativeState.Primitive0;
        ulong passCompatibility = Mix(
            unchecked((ulong)(uint)variant.PassIndex),
            variant.OutputProfileVariant);
        ulong pipelineVariant = Mix(
            structuralIdentity.PipelineSignature,
            primitive.Pipeline.Handle,
            unchecked((ulong)variant.SubmissionStrategy),
            variant.InstrumentationSchema,
            unchecked((ulong)variant.MeshDialect));
        ulong geometryPage = Mix(
            structuralIdentity.GeometrySignature,
            primitive.Indexed ? primitive.IndexBuffer.Handle : 1u,
            nativeState.VertexBindingSignature);
        uint topologyAndIndexType = unchecked(
            ((uint)primitive.Topology << 16) | (uint)primitive.IndexType);
        ulong descriptorModel = Mix(
            structuralIdentity.DependencySignature,
            structuralIdentity.ProgramSignature);
        return new(
            passCompatibility,
            pipelineVariant,
            geometryPage,
            topologyAndIndexType,
            descriptorModel,
            viewMask,
            orderingClass,
            new VulkanRenderBinNativeCompatibility(in nativeState),
            contextCompatibility);
    }

    /// <summary>Builds an exact canonical packed-geometry visibility key.</summary>
    internal static VulkanRenderBinKey CreateVisibilityGeometry(
        int passIndex,
        uint viewMask,
        in AdvancedVisibilityPayload payload,
        ulong sceneNativeGeneration,
        VulkanFrameDataSlice vertexSlice,
        VulkanFrameDataSlice indexSlice,
        in VulkanResidentDrawTemplateNativeState nativeState,
        in FrameOpContext context)
    {
        VulkanPreparedMeshPrimitive primitive = nativeState.Primitive0;
        uint acceptedViewMask = viewMask == 0u ? 1u : viewMask;
        return new(
            Mix(unchecked((ulong)(uint)(passIndex + 1)), acceptedViewMask),
            Mix(payload.RasterStateClass, checked((ulong)payload.Coverage),
                payload.CullMode, payload.PrimitiveTopology),
            Mix(payload.Geometry.Index, payload.Geometry.Generation,
                sceneNativeGeneration, vertexSlice.Generation,
                indexSlice.Generation, primitive.IndexBuffer.Handle,
                nativeState.VertexBindingSignature),
            unchecked(((uint)primitive.Topology << 16) | (uint)primitive.IndexType),
            1u,
            acceptedViewMask,
            VulkanRenderBinOrderingClass.Opaque,
            new VulkanRenderBinNativeCompatibility(in nativeState),
            VulkanRenderBinContextCompatibility.Create(in context));
    }

    private static ulong Mix(params ReadOnlySpan<ulong> values)
    {
        ulong hash = 14695981039346656037UL;
        for (int index = 0; index < values.Length; ++index)
            hash = (hash ^ values[index]) * 1099511628211UL;
        return hash == 0u ? 1u : hash;
    }
}

/// <summary>Ordering contract for a bin; ordered work never enters opaque bins.</summary>
internal enum VulkanRenderBinOrderingClass : byte
{
    Opaque = 0,
    Transparent = 1,
    Ui = 2,
    Callback = 3,
    UnsupportedCustom = 4,
}
