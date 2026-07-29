using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Canonical cross-domain visibility rules shared by OpenGL and Vulkan.
/// Vulkan lowers the stage/access states through legacy barriers or synchronization2;
/// OpenGL lowers the matching explicit barrier mask and preserves command order.
/// </summary>
public static class AdvancedSynchronizationContract
{
    private static readonly AdvancedSynchronizationBoundaryDescriptor[] Boundaries =
    [
        new(
            EAdvancedSynchronizationBoundary.ComputePreparationToVisibilityRaster,
            EAdvancedRenderStage.VisibilityPreparation,
            EAdvancedRenderStage.VisibilityRaster,
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
            EAdvancedOpenGlMemoryBarrier.Command |
            EAdvancedOpenGlMemoryBarrier.VertexAttributeArray |
            EAdvancedOpenGlMemoryBarrier.ElementArray |
            EAdvancedOpenGlMemoryBarrier.ShaderStorage),
        new(
            EAdvancedSynchronizationBoundary.VisibilityRasterToComputeShading,
            EAdvancedRenderStage.VisibilityRaster,
            EAdvancedRenderStage.DepthPyramidAndLateVisibility,
            new RenderGraphSyncState(
                RenderGraphStageMask.ColorAttachmentOutput |
                RenderGraphStageMask.EarlyFragmentTests |
                RenderGraphStageMask.LateFragmentTests,
                RenderGraphAccessMask.ColorAttachmentWrite |
                RenderGraphAccessMask.DepthStencilWrite,
                Layout: null),
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderRead |
                RenderGraphAccessMask.ShaderWrite,
                Layout: null),
            EAdvancedOpenGlMemoryBarrier.FrameBuffer |
            EAdvancedOpenGlMemoryBarrier.TextureFetch |
            EAdvancedOpenGlMemoryBarrier.ShaderImageAccess |
            EAdvancedOpenGlMemoryBarrier.ShaderStorage),
        new(
            EAdvancedSynchronizationBoundary.ComputeShadingToLateGraphics,
            EAdvancedRenderStage.NativeOpaqueShading,
            EAdvancedRenderStage.LatePasses,
            new RenderGraphSyncState(
                RenderGraphStageMask.ComputeShader,
                RenderGraphAccessMask.ShaderWrite,
                Layout: null),
            new RenderGraphSyncState(
                RenderGraphStageMask.AllGraphics |
                RenderGraphStageMask.DrawIndirect,
                RenderGraphAccessMask.ShaderRead |
                RenderGraphAccessMask.ColorAttachmentRead |
                RenderGraphAccessMask.ColorAttachmentWrite |
                RenderGraphAccessMask.IndirectCommandRead,
                Layout: null),
            EAdvancedOpenGlMemoryBarrier.Command |
            EAdvancedOpenGlMemoryBarrier.TextureFetch |
            EAdvancedOpenGlMemoryBarrier.ShaderImageAccess |
            EAdvancedOpenGlMemoryBarrier.ShaderStorage |
            EAdvancedOpenGlMemoryBarrier.FrameBuffer),
        new(
            EAdvancedSynchronizationBoundary.LateGraphicsToPresentation,
            EAdvancedRenderStage.UserInterface,
            ConsumerStage: null,
            new RenderGraphSyncState(
                RenderGraphStageMask.AllGraphics,
                RenderGraphAccessMask.ColorAttachmentWrite |
                RenderGraphAccessMask.ShaderWrite,
                RenderGraphImageLayout.ColorAttachment),
            new RenderGraphSyncState(
                RenderGraphStageMask.AllCommands,
                RenderGraphAccessMask.MemoryRead,
                RenderGraphImageLayout.Present),
            EAdvancedOpenGlMemoryBarrier.TextureFetch |
            EAdvancedOpenGlMemoryBarrier.ShaderImageAccess |
            EAdvancedOpenGlMemoryBarrier.FrameBuffer),
    ];

    private static readonly ReadOnlyCollection<AdvancedSynchronizationBoundaryDescriptor> OrderedBoundaries =
        Array.AsReadOnly(Boundaries);

    /// <summary>
    /// Ordered logical synchronization boundaries.
    /// </summary>
    public static IReadOnlyList<AdvancedSynchronizationBoundaryDescriptor> Ordered
        => OrderedBoundaries;

    /// <summary>
    /// Resolves a boundary descriptor without allocating.
    /// </summary>
    public static AdvancedSynchronizationBoundaryDescriptor Get(
        EAdvancedSynchronizationBoundary boundary)
    {
        int index = (int)boundary;
        if ((uint)index >= (uint)Boundaries.Length ||
            Boundaries[index].Boundary != boundary)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundary),
                boundary,
                "Unknown advanced synchronization boundary.");
        }

        return Boundaries[index];
    }

    /// <summary>
    /// Verifies that a selected synchronization encoding belongs to the active backend.
    /// </summary>
    public static bool IsEncodingCompatible(
        RuntimeGraphicsApiKind backend,
        EAdvancedSynchronizationMode mode)
        => (backend, mode) switch
        {
            (RuntimeGraphicsApiKind.OpenGL, EAdvancedSynchronizationMode.OpenGlMemoryBarrier)
                => true,
            (RuntimeGraphicsApiKind.Vulkan, EAdvancedSynchronizationMode.VulkanLegacyBarriers)
                => true,
            (RuntimeGraphicsApiKind.Vulkan, EAdvancedSynchronizationMode.VulkanSynchronization2)
                => true,
            _ => false,
        };
}
