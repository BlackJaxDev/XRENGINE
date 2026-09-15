using Silk.NET.OpenXR;
using System.Collections.Generic;
using System.Threading;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanXrGraphicsBinding
{
    private readonly List<RetiredOpenXrSwapchainGeneration> _deviceLossQuarantine = [];
    private int _deviceLossAbandonedGenerationCount;
    private int _deviceLossAbandonedSwapchainCount;
    private int _deviceLossAbandonedAcquiredSwapchainCount;

    /// <summary>
    /// Moves all Vulkan/OpenXR swapchain ownership into a terminal quarantine.
    /// No native destroy, release, free, timeline query, or resource retirement is legal here.
    /// </summary>
    public OpenXrDeviceLossBindingAbandonment AbandonAfterDeviceLoss(
        OpenXRAPI api,
        AbstractRenderer renderer,
        string reason)
    {
        Attach(api);
        if (renderer is not VulkanRenderer vulkanRenderer || !vulkanRenderer.IsDeviceLost)
            return default;

        lock (_retiredSwapchainsGate)
        {
            if (_deviceLossAbandonedGenerationCount != 0 || _deviceLossQuarantine.Count != 0)
                return new OpenXrDeviceLossBindingAbandonment(
                    _deviceLossAbandonedGenerationCount,
                    _deviceLossAbandonedSwapchainCount,
                    _deviceLossAbandonedAcquiredSwapchainCount);

            int activeSwapchainCount = 0;
            Swapchain[] activeSwapchains = new Swapchain[_swapchains.Length];
            uint[] activeImageCounts = new uint[_swapchainImageCounts.Length];
            SwapchainImageVulkan2KHR*[] activeImages = new SwapchainImageVulkan2KHR*[_swapchainImagesVK.Length];
            for (int i = 0; i < activeSwapchains.Length; ++i)
            {
                activeSwapchains[i] = _swapchains[i];
                activeImageCounts[i] = _swapchainImageCounts[i];
                activeImages[i] = _swapchainImagesVK[i];
                if (activeSwapchains[i].Handle != 0)
                    activeSwapchainCount++;
                _swapchainImagesVK[i] = null;
            }

            if (activeSwapchainCount != 0)
            {
                _deviceLossQuarantine.Add(new RetiredOpenXrSwapchainGeneration(
                    activeSwapchains, activeImages, activeImageCounts, _viewCount,
                    0, default, false, default, false, [], [], false,
                    default, false, System.Diagnostics.Stopwatch.GetTimestamp(), -1));
                _deviceLossAbandonedGenerationCount++;
                _deviceLossAbandonedSwapchainCount += activeSwapchainCount;
            }

            for (int i = 0; i < _retiredSwapchainGenerations.Count; ++i)
            {
                RetiredOpenXrSwapchainGeneration generation = _retiredSwapchainGenerations[i];
                _deviceLossQuarantine.Add(generation);
                _deviceLossAbandonedGenerationCount++;
                for (int view = 0; view < generation.Swapchains.Length; ++view)
                    if (generation.Swapchains[view].Handle != 0)
                        _deviceLossAbandonedSwapchainCount++;
            }
            _retiredSwapchainGenerations.Clear();
            _deviceLossAbandonedAcquiredSwapchainCount += _runtimeAcquiredSwapchainHandles.Count;
            _runtimeAcquiredSwapchainHandles.Clear();
            _lastRetirementAdmissionBlockers |= EOpenXrSwapchainRetirementBlockers.DeviceLost;

            return new OpenXrDeviceLossBindingAbandonment(
                _deviceLossAbandonedGenerationCount,
                _deviceLossAbandonedSwapchainCount,
                _deviceLossAbandonedAcquiredSwapchainCount);
        }
    }
}
