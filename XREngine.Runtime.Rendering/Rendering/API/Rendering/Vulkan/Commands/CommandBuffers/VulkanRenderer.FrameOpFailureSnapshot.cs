namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct FrameOpFailureSnapshot(
            string OpType,
            int PassIndex,
            int PipelineIdentity,
            int ViewportIdentity,
            string TargetName,
            string MaterialName,
            string ShaderName,
            string Message);

    }
}
