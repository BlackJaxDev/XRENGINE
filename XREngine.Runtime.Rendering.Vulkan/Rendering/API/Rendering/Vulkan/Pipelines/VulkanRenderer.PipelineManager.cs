namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void QueueProgramLinkUntilDeviceReady(VkRenderProgram program)
        => ResourceRuntime.PipelineManager.QueueProgramLinkUntilDeviceReady(program);

    private void FlushPendingDeviceReadyProgramLinks()
    {
        if (!_deviceContext.IsReady)
            return;

        int deferredCount = ResourceRuntime.PipelineManager.FlushPendingDeviceReadyProgramLinks();
        if (deferredCount == 0)
            return;

        // Programs link on first use. First-frame acceptance warms only resources
        // required by the active view instead of compiling every logical program.
        Debug.Vulkan(
            $"Deferred {deferredCount} Vulkan program link(s) until first use after logical device creation.");
    }

    private void ClearPendingDeviceReadyProgramLinks()
        => ResourceRuntime.PipelineManager.ClearPendingDeviceReadyProgramLinks();
}
