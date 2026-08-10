using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one registry that contributes to a merged frame-operation registry.</summary>
internal readonly record struct FrameOpRegistryCacheSource(
    RenderResourceRegistry Registry,
    int DescriptorSignature);
