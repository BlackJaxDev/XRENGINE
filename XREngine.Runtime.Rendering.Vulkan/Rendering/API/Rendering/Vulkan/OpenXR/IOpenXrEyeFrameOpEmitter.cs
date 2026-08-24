namespace XREngine.Rendering.Vulkan;

/// <summary>Produces the render-graph operations for a typed OpenXR eye request.</summary>
internal interface IOpenXrEyeFrameOpEmitter
{
    void Emit(in OpenXrEyeFrameOpEmission emission);
}
