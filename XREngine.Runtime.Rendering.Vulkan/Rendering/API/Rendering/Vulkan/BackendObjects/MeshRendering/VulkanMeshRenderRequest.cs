using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
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
    bool ForceNoStereo,
    AdvancedGpuSceneDrawIdentitySnapshot CanonicalDrawIdentitySnapshot,
    VulkanResidentDrawTemplateHandle ResidentTemplateHandle,
    WindowPresentationSourceMarker WindowPresentationSourceMarker)
{
    internal VulkanMeshIndexedIndirectPayload? IndexedIndirect { get; init; }

    internal static void ReleaseAuthoringLease(ref VulkanMeshRenderRequest request)
    {
        if (request.IndexedIndirect is not { AuthoringLease: { } owner } payload)
            return;
        request = request with
        {
            IndexedIndirect = payload with { AuthoringLease = null },
        };
        owner.ReleaseAuthoringUse();
    }

    internal static void ReleaseAuthoringLeasesAndClear(
        Span<VulkanMeshRenderRequest> requests)
    {
        for (int index = 0; index < requests.Length; ++index)
            ReleaseAuthoringLease(ref requests[index]);
        requests.Clear();
    }
}
