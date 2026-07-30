namespace XREngine.Rendering.Vulkan;

internal enum AttachmentRole
{
    Unused,
    Color,
    Resolve,
    Depth,
    DepthStencil,
    Stencil,
}
