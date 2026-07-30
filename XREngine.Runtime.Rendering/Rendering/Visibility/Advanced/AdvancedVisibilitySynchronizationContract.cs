using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Executable early/HZB/late synchronization contract. OpenGL executes the
/// listed memory barrier after each producer. Vulkan render-graph lowering
/// consumes the paired stage/access states and image layouts.
/// </summary>
public static class AdvancedVisibilitySynchronizationContract
{
    private static readonly AdvancedVisibilitySynchronizationBoundaryDescriptor[]
        Boundaries =
    [
        new(
            EAdvancedVisibilitySynchronizationBoundary.PreparationToEarlyRaster,
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderWrite,
                Layout: null),
            new RenderGraphSyncState(
                RenderGraphStageMask.DrawIndirect |
                RenderGraphStageMask.VertexInput |
                RenderGraphStageMask.VertexShader,
                RenderGraphAccessMask.IndirectCommandRead |
                RenderGraphAccessMask.VertexAttributeRead |
                RenderGraphAccessMask.IndexRead |
                RenderGraphAccessMask.ShaderRead,
                Layout: null),
            EMemoryBarrierMask.Command |
            EMemoryBarrierMask.VertexAttribArray |
            EMemoryBarrierMask.ElementArray |
            EMemoryBarrierMask.ShaderStorage),
        new(
            EAdvancedVisibilitySynchronizationBoundary.EarlyRasterToDepthPyramid,
            new RenderGraphSyncState(
                RenderGraphStageMask.ColorAttachmentOutput |
                RenderGraphStageMask.EarlyFragmentTests |
                RenderGraphStageMask.LateFragmentTests,
                RenderGraphAccessMask.ColorAttachmentWrite |
                RenderGraphAccessMask.DepthStencilWrite,
                RenderGraphImageLayout.DepthStencilAttachment),
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderRead |
                RenderGraphAccessMask.ShaderWrite,
                RenderGraphImageLayout.General),
            EMemoryBarrierMask.Framebuffer |
            EMemoryBarrierMask.TextureFetch |
            EMemoryBarrierMask.ShaderImageAccess),
        new(
            EAdvancedVisibilitySynchronizationBoundary.DepthPyramidToLatePreparation,
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderWrite,
                RenderGraphImageLayout.General),
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderRead,
                RenderGraphImageLayout.ShaderReadOnly),
            EMemoryBarrierMask.ShaderImageAccess |
            EMemoryBarrierMask.TextureFetch),
        new(
            EAdvancedVisibilitySynchronizationBoundary.LatePreparationToLateRaster,
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderWrite,
                Layout: null),
            new RenderGraphSyncState(
                RenderGraphStageMask.AllGraphics |
                RenderGraphStageMask.DrawIndirect,
                RenderGraphAccessMask.IndirectCommandRead |
                RenderGraphAccessMask.ShaderRead,
                Layout: null),
            EMemoryBarrierMask.Command |
            EMemoryBarrierMask.ShaderStorage),
        new(
            EAdvancedVisibilitySynchronizationBoundary.LateRasterToConsumers,
            new RenderGraphSyncState(
                RenderGraphStageMask.ColorAttachmentOutput |
                RenderGraphStageMask.EarlyFragmentTests |
                RenderGraphStageMask.LateFragmentTests,
                RenderGraphAccessMask.ColorAttachmentWrite |
                RenderGraphAccessMask.DepthStencilWrite,
                RenderGraphImageLayout.DepthStencilAttachment),
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderRead,
                RenderGraphImageLayout.ShaderReadOnly),
            EMemoryBarrierMask.Framebuffer |
            EMemoryBarrierMask.TextureFetch |
            EMemoryBarrierMask.ShaderImageAccess |
            EMemoryBarrierMask.ShaderStorage),
    ];

    private static readonly ReadOnlyCollection<
        AdvancedVisibilitySynchronizationBoundaryDescriptor> OrderedBoundaries =
        Array.AsReadOnly(Boundaries);

    public static IReadOnlyList<
        AdvancedVisibilitySynchronizationBoundaryDescriptor> Ordered
        => OrderedBoundaries;

    public static AdvancedVisibilitySynchronizationBoundaryDescriptor Get(
        EAdvancedVisibilitySynchronizationBoundary boundary)
    {
        int index = (int)boundary;
        if ((uint)index >= (uint)Boundaries.Length)
            throw new ArgumentOutOfRangeException(nameof(boundary));
        return Boundaries[index];
    }

    public static void ApplyOpenGl(
        EAdvancedVisibilitySynchronizationBoundary boundary)
        => AbstractRenderer.Current?.MemoryBarrier(
            Get(boundary).OpenGlBarrierMask);
}
