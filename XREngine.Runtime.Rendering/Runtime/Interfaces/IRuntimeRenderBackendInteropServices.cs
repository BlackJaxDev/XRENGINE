using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Required backend-neutral interop operations used by renderer implementations.
/// </summary>
public interface IRuntimeRenderBackendInteropServices
{

    /// <summary>
    /// Destroys host-owned API render objects associated with a renderer.
    /// </summary>
    void DestroyObjectsForRenderer(IRuntimeRendererHost renderer);

    /// <summary>
    /// Gets whether the supplied viewport is currently present on the host rendering viewport stack.
    /// </summary>
    bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport);

    /// <summary>
    /// Gets whether the host requests a debug opaque pipeline override.
    /// </summary>
    bool ShouldForceDebugOpaquePipeline { get; }

    /// <summary>
    /// Creates a debug opaque pipeline override for diagnosing transparency or material binding issues.
    /// </summary>
    IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride();

    /// <summary>
    /// Prepares host-owned upscaling or interop resources for the supplied viewport and pipeline frame.
    /// </summary>
    void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline);

    /// <summary>
    /// Applies host-level material shader binding configuration before a material program is used.
    /// </summary>
    void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program);

    /// <summary>
    /// Gets the byte size of one pixel for a sized internal texture format.
    /// </summary>
    int GetBytesPerPixel(ESizedInternalFormat format);

    /// <summary>
    /// Gets the byte size of one pixel for a renderbuffer storage format.
    /// </summary>
    int GetBytesPerPixel(ERenderBufferStorage storage);

    /// <summary>
    /// Adds framebuffer bandwidth usage, in bytes, to host render statistics.
    /// </summary>
    void AddFrameBufferBandwidth(long totalBytes);

    /// <summary>
    /// Dispatches a compute program through the active host renderer.
    /// </summary>
    void DispatchCompute(XRRenderProgram program, uint groupCountX, uint groupCountY, uint groupCountZ);

    /// <summary>
    /// Attempts to blit between two framebuffers through the active host renderer.
    /// </summary>
    bool TryBlitFrameBufferToFrameBuffer(
        XRFrameBuffer sourceFrameBuffer,
        XRFrameBuffer destinationFrameBuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);

    /// <summary>
    /// Attempts to blit from a viewport grab source to a framebuffer through the active host renderer.
    /// </summary>
    bool TryBlitViewportToFrameBuffer(
        IRuntimeViewportGrabSource viewport,
        XRFrameBuffer framebuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);
}
