namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void QueueProgramLinkUntilDeviceReady(VkRenderProgram program)
        => _pipelineManager.QueueProgramLinkUntilDeviceReady(program);

    private void FlushPendingDeviceReadyProgramLinks()
    {
        if (!IsLogicalDeviceReady)
            return;

        int deferredCount = _pipelineManager.FlushPendingDeviceReadyProgramLinks();
        if (deferredCount == 0)
            return;

        // Programs link on first use. First-frame acceptance warms only resources
        // required by the active view instead of compiling every logical program.
        Debug.Vulkan(
            $"Deferred {deferredCount} Vulkan program link(s) until first use after logical device creation.");
    }

    private void ClearPendingDeviceReadyProgramLinks()
        => _pipelineManager.ClearPendingDeviceReadyProgramLinks();
}
