namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct FrameOpSignatureDebugPart(
            int OpIndex,
            string OpType,
            string Component,
            ulong Signature,
            string Detail);

    }
}
