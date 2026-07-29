namespace XREngine.Rendering.Vulkan;

[Flags]
internal enum CommandChainQueueEligibility
{
    None = 0,
    Graphics = 1 << 0,
    Compute = 1 << 1,
    Transfer = 1 << 2,
    SecondaryGraphics = 1 << 3,
}
