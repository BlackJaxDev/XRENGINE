namespace XREngine.Rendering.Vulkan;

/// <summary>Native execution scope required by one immutable packet.</summary>
internal enum RenderPacketExecutionDomain
{
    GraphicsRendering = 0,
    StandaloneCompute = 1,
    StandaloneSynchronization = 2,
    StandaloneTransfer = 3,
}
