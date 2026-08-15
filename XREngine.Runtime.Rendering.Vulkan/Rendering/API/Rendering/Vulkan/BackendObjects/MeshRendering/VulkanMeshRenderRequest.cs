using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Raw render-event facts captured by a mesh wrapper.  It intentionally contains
/// no Vulkan authority or mutable rendering state. Producer target and raster
/// facts are frozen while their engine scopes are active, then FrameLoop adds
/// only output-authority facts when it drains the request.
/// </summary>
internal readonly record struct VulkanMeshRenderRequest(
    VkMeshRenderer Renderer,
    int PassIndex,
    XRRenderPipelineInstance? Pipeline,
    FrameOpContext Context,
    VulkanMeshProducerSnapshot Producer,
    DeferredRenderBindingPublication DeferredBindings,
    ResolvedMeshRenderMaterial ResolvedMaterial,
    VulkanMeshDrawViewSnapshot ViewSnapshot,
    LayeredShadowCasterRelevance ShadowCasterRelevance,
    uint TransformId,
    ulong PreparationCompatibilitySignature,
    Matrix4x4 ModelMatrix,
    Matrix4x4 PreviousModelMatrix,
    XRMaterial? MaterialOverride,
    RenderingParameters? RenderOptionsOverride,
    uint Instances,
    uint ExpandedInstances,
    EMeshBillboardMode BillboardMode,
    bool ForceNoStereo);
