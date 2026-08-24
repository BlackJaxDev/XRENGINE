using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the command and synchronization sidecars for one detached ImGui
/// platform viewport. The corresponding WSI images live in the output
/// authority; this service deliberately has no renderer facade dependency.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal unsafe VulkanImGuiPlatformWindowCommandResources CreateImGuiPlatformWindowResources(
        VulkanDeviceContext device,
        VulkanTargetOutputContext target,
        uint graphicsQueueFamily,
        int framesInFlight,
        int swapchainImageCount,
        uint viewportId)
    {
        if (framesInFlight <= 0 || swapchainImageCount <= 0)
            throw new ArgumentOutOfRangeException(
                framesInFlight <= 0 ? nameof(framesInFlight) : nameof(swapchainImageCount));

        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = graphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit,
        };
        ThrowIfFailed(
            target.CreateVulkanCommandPoolTracked(ref poolInfo, out CommandPool commandPool, $"ImGuiViewport[{viewportId:X8}].CommandPool"),
            "create detached-window command pool");

        CommandBuffer[] commandBuffers = new CommandBuffer[framesInFlight];
        Fence[] fences = new Fence[framesInFlight];
        Semaphore[] imageAvailable = new Semaphore[framesInFlight];
        Semaphore[] renderFinished = new Semaphore[swapchainImageCount];
        try
        {
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            for (int index = 0; index < commandBuffers.Length; index++)
            {
                ThrowIfFailed(
                    target.AllocateVulkanCommandBufferTracked(
                        ref allocateInfo,
                        out commandBuffers[index],
                        $"ImGuiViewport[{viewportId:X8}].CommandBuffer[{index}]"),
                    "allocate detached-window command buffer");
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocateCommandBuffersCall(1, true);
            }

            FenceCreateInfo fenceInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };
            SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
            for (int index = 0; index < framesInFlight; index++)
            {
                ThrowIfFailed(device.Api.CreateFence(device.Device, in fenceInfo, null, out fences[index]), "create detached-window frame fence");
                ThrowIfFailed(device.Api.CreateSemaphore(device.Device, in semaphoreInfo, null, out imageAvailable[index]), "create detached-window acquire semaphore");
            }
            for (int index = 0; index < renderFinished.Length; index++)
                ThrowIfFailed(device.Api.CreateSemaphore(device.Device, in semaphoreInfo, null, out renderFinished[index]), "create detached-window render-finished semaphore");

            return new VulkanImGuiPlatformWindowCommandResources(
                commandPool,
                commandBuffers,
                fences,
                new bool[framesInFlight],
                imageAvailable,
                renderFinished);
        }
        catch
        {
            DestroyImGuiPlatformWindowResources(
                device,
                target,
                new VulkanImGuiPlatformWindowCommandResources(
                    commandPool,
                    commandBuffers,
                    fences,
                    new bool[framesInFlight],
                    imageAvailable,
                    renderFinished),
                viewportId);
            throw;
        }
    }

    internal unsafe void DestroyImGuiPlatformWindowResources(
        VulkanDeviceContext device,
        VulkanTargetOutputContext target,
        in VulkanImGuiPlatformWindowCommandResources resources,
        uint viewportId)
    {
        for (int index = 0; index < resources.Fences.Length; index++)
            if (resources.Fences[index].Handle != 0)
                device.Api.DestroyFence(device.Device, resources.Fences[index], null);
        for (int index = 0; index < resources.ImageAvailableSemaphores.Length; index++)
            if (resources.ImageAvailableSemaphores[index].Handle != 0)
                device.Api.DestroySemaphore(device.Device, resources.ImageAvailableSemaphores[index], null);
        for (int index = 0; index < resources.RenderFinishedSemaphores.Length; index++)
            if (resources.RenderFinishedSemaphores[index].Handle != 0)
                device.Api.DestroySemaphore(device.Device, resources.RenderFinishedSemaphores[index], null);

        for (int index = 0; index < resources.CommandBuffers.Length; index++)
            RemoveCommandBufferBindState(resources.CommandBuffers[index]);
        if (resources.CommandPool.Handle != 0)
            target.DestroyCommandPoolHostSynchronized(resources.CommandPool);
    }

    private static void ThrowIfFailed(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }
}
