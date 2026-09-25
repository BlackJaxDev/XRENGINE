namespace XREngine.Rendering.Vulkan;

/// <summary>Produces the render-graph operations for a typed OpenXR eye request.</summary>
internal interface IOpenXrEyeFrameOpEmitter
{
    /// <summary>Returns false when eye resources are not ready for this frame.</summary>
    bool TryEmit(in OpenXrEyeFrameOpEmission emission);
}
