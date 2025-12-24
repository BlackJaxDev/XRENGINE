using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private Semaphore[]? imageAvailableSemaphores;
    private Semaphore[]? renderFinishedSemaphores;
    private Fence[]? inFlightFences;
    private Fence[]? imagesInFlight;

    private void DestroySyncObjects()
    {
        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            Api!.DestroySemaphore(device, renderFinishedSemaphores![i], null);
            Api!.DestroySemaphore(device, imageAvailableSemaphores![i], null);
            Api!.DestroyFence(device, inFlightFences![i], null);
        }
    }

    private void CreateSyncObjects()
    {
        imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];
        imagesInFlight = new Fence[swapChainImages!.Length];

        SemaphoreCreateInfo semaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
        };

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (var i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            if (Api!.CreateSemaphore(device, ref semaphoreInfo, null, out imageAvailableSemaphores[i]) != Result.Success ||
                Api!.CreateSemaphore(device, ref semaphoreInfo, null, out renderFinishedSemaphores[i]) != Result.Success ||
                Api!.CreateFence(device, ref fenceInfo, null, out inFlightFences[i]) != Result.Success)
                throw new Exception("failed to create synchronization objects for a frame!");
    }
}