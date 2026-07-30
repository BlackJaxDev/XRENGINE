using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral synchronization required by reconstruction and its captures.
/// </summary>
public static class AdvancedReconstructionSynchronizationContract
{
    private static readonly AdvancedReconstructionSynchronizationBoundaryDescriptor[]
        Boundaries =
    [
        new(
            EAdvancedReconstructionSynchronizationBoundary.FinalVisibilityToReconstruction,
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
            EMemoryBarrierMask.ShaderStorage),
        new(
            EAdvancedReconstructionSynchronizationBoundary.ReconstructionDiagnosticsToCapture,
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderWrite,
                RenderGraphImageLayout.General),
            new RenderGraphSyncState(
                RenderGraphStageMask.Transfer |
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.TransferRead |
                RenderGraphAccessMask.ShaderRead,
                RenderGraphImageLayout.TransferSource),
            EMemoryBarrierMask.ShaderImageAccess |
            EMemoryBarrierMask.TextureFetch |
            EMemoryBarrierMask.PixelBuffer),
    ];

    private static readonly ReadOnlyCollection<
        AdvancedReconstructionSynchronizationBoundaryDescriptor> OrderedBoundaries =
        Array.AsReadOnly(Boundaries);

    public static IReadOnlyList<
        AdvancedReconstructionSynchronizationBoundaryDescriptor> Ordered
        => OrderedBoundaries;

    public static AdvancedReconstructionSynchronizationBoundaryDescriptor Get(
        EAdvancedReconstructionSynchronizationBoundary boundary)
    {
        int index = (int)boundary;
        if ((uint)index >= (uint)Boundaries.Length)
            throw new ArgumentOutOfRangeException(nameof(boundary));
        return Boundaries[index];
    }

    public static void ApplyOpenGl(
        EAdvancedReconstructionSynchronizationBoundary boundary)
        => AbstractRenderer.Current?.MemoryBarrier(
            Get(boundary).OpenGlBarrierMask);
}
