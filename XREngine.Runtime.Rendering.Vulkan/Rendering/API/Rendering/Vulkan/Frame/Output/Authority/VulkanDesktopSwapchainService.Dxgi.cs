using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// DXGI flip-model generations of the desktop output. A DXGI generation uses the
/// same image views, render passes, framebuffers, present bridges, and retirement
/// flow as a native generation. Only the native create, acquire, present, and
/// destroy calls differ.
/// </summary>
internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    /// <summary>
    /// Creates the live generation on the DXGI path when the window requests it.
    /// Returns false when the native Vulkan swapchain must be used instead; the
    /// reason is logged once and the output keeps the native path after that.
    /// </summary>
    private bool TryCreateDxgiGeneration()
    {
        VulkanDesktopWsiTargetDriver? target = _desktopWsiTarget;
        if (target is null || target.RequestedPresentPath != EDesktopPresentPath.DxgiFlipModel)
        {
            ReleaseDxgiPresenterForNativePath();
            return false;
        }

        if (_output.Desktop.DxgiStartupFailed)
            return false;

        SwapChainSupportDetails support = QuerySwapchainSupport(_device.PhysicalDevice);
        VulkanPresentationProfileResolution presentation =
            VulkanPresentationProfileResolver.Resolve(
                support.PresentModes,
                _device,
                _output,
                _frameSlotCount,
                _telemetry._diagnosticOptions.EnableValidationLayers);
        if (presentation.Snapshot.FrameGenerationEnabled)
        {
            FailDxgiStartup("NVIDIA DLSS frame generation needs the Streamline proxy Vulkan swapchain");
            return false;
        }

        if (!TryChooseExtent(support.Capabilities, out Extent2D extent, out string unavailableReason))
            throw new InvalidOperationException($"Cannot create the DXGI present generation while the surface is not presentable: {unavailableReason}");

        VulkanDxgiPresenter? presenter = _output.Desktop.DxgiPresenter;
        if (presenter is { Faulted: true })
        {
            FailDxgiStartup($"the DXGI presenter failed earlier: {presenter.FaultReason}");
            return false;
        }

        if (presenter is null)
        {
            if (!VulkanDxgiPresenter.TryCreate(_api, _device, target.OperatingSystemWindowHandle, null, out presenter, out string createFailure))
            {
                FailDxgiStartup(createFailure);
                return false;
            }

            _output.Desktop.DxgiPresenter = presenter;
        }

        if (!presenter!.TryConfigureBuffers(extent.Width, extent.Height, target.PreferHdrOutput, out string configureFailure))
        {
            FailDxgiStartup(configureFailure);
            return false;
        }

        ulong generation = _output.Desktop.Generation + 1;
        if (!VulkanDxgiImageRing.TryCreate(_device, presenter, extent, generation, out VulkanDxgiImageRing? ring, out string ringFailure))
        {
            FailDxgiStartup(ringFailure);
            return false;
        }

        // Platform windows of ImGui keep native swapchains through this extension.
        if (!_api.TryGetDeviceExtension(_device.Instance, _device.Device, out KhrSwapchain? swapchainExtension) ||
            swapchainExtension is null)
        {
            ring!.Dispose();
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");
        }

        PublishDxgiGeneration(ring!, presentation, swapchainExtension, extent);
        return true;
    }

    private void PublishDxgiGeneration(
        VulkanDxgiImageRing ring,
        VulkanPresentationProfileResolution presentation,
        KhrSwapchain swapchainExtension,
        Extent2D extent)
    {
        int imageCount = ring.Images.Length;
        PresentModeKHR presentMode = presentation.NativePresentMode;
        _output.Desktop.SwapchainExtension = swapchainExtension;
        _output.Desktop.Swapchain = ring.Sentinel;
        _output.Desktop.DxgiRing = ring;
        _output.Desktop.Generation++;
        _output.Desktop.PresentScalingActive = false;
        _output.Desktop.PresentScalingCapabilities = default;
        _output.Desktop.StreamlineFrameGenerationActive = false;
        _output.Desktop.StreamlineFrameGenerationIncludesDlss = false;
        _output.Desktop.DxgiSyncInterval = presentMode is PresentModeKHR.FifoKhr or PresentModeKHR.FifoRelaxedKhr ? 1u : 0u;
        _output.Desktop.DxgiAllowTearing = presentMode == PresentModeKHR.ImmediateKhr;

        Image[] images = new Image[imageCount];
        Array.Copy(ring.Images, images, imageCount);
        _output.Desktop.Images = images;
        _resources.Descriptors.EnsureFrameSlotCountFloor(imageCount);
        _output.Desktop.ImageEverPresented = new bool[imageCount];
        _output.Desktop.ImageHasValidPresentedContent = new bool[imageCount];
        for (int index = 0; index < images.Length; index++)
            _resources.Images.ClearTrackedLayouts(images[index]);

        // The DXGI present submission signals the reservation fence, so presentation
        // release has the same fence proof as a maintenance1 swapchain.
        _output.Desktop.PresentCompletion = new VulkanWsiPresentCompletion(
            _api, _device.Device, imageCount, maintenanceEnabled: true);

        VulkanDxgiSurfaceFormat format = ring.SurfaceFormat;
        _output.Desktop.PreferredFormat = format.VulkanFormat;
        _output.Desktop.PreferredColorSpace = format.VulkanColorSpace;
        _output.Desktop.ImageFormat = format.VulkanFormat;
        _output.Desktop.ImageColorSpace = format.VulkanColorSpace;
        _output.Desktop.Extent = extent;
        _output.Desktop.PresentationProfile = presentation.Snapshot with
        {
            SwapchainImageCount = imageCount,
            FrameGenerationEnabled = false,
        };

        string message =
            $"present path=DxgiFlipModel format={format.Name} extent={extent.Width}x{extent.Height} images={imageCount} " +
            $"syncInterval={_output.Desktop.DxgiSyncInterval} tearing={_output.Desktop.DxgiAllowTearing && _output.Desktop.DxgiPresenter!.TearingSupported} " +
            $"profile={presentation.Snapshot.RequestedProfile}->{presentation.Snapshot.ResolvedProfile}";
        Debug.VulkanWarningEvery(
            "Vulkan.Swapchain.SelectedSurfaceFormat",
            TimeSpan.FromSeconds(10),
            "[Vulkan] Desktop {0}",
            message);
        Debug.WriteAuxiliaryLog(VulkanDxgiPresenter.LogFileName, message);
    }

    private void FailDxgiStartup(string reason)
    {
        _output.Desktop.DxgiStartupFailed = true;
        ReleaseDxgiPresenterForNativePath();
        Debug.VulkanWarning(
            "[Vulkan][DxgiPresent] The DXGI flip-model present path cannot start; the native Vulkan swapchain is used. Reason={0}",
            reason);
        Debug.WriteAuxiliaryLog(VulkanDxgiPresenter.LogFileName, $"fallback to VulkanSwapchain: {reason}");
    }

    /// <summary>
    /// Releases the DXGI presenter so that a native Vulkan swapchain can own the window.
    /// A window accepts only one flip-model swapchain, so the presenter must go first.
    /// </summary>
    private void ReleaseDxgiPresenterForNativePath()
    {
        VulkanDxgiPresenter? presenter = _output.Desktop.DxgiPresenter;
        if (presenter is null)
            return;

        _output.Desktop.DxgiPresenter = null;
        presenter.Dispose();
    }

    /// <summary>Returns the old swapchain for native recreation, without a DXGI sentinel.</summary>
    private static SwapchainKHR NativeOldSwapchain(SwapchainKHR oldSwapchain)
        => VulkanDxgiImageRing.IsSentinel(oldSwapchain) ? default : oldSwapchain;

    /// <summary>Detaches the live DXGI ring so that it can retire with its generation.</summary>
    private VulkanDxgiImageRing? DetachLiveDxgiRing()
    {
        VulkanDxgiImageRing? ring = _output.Desktop.DxgiRing;
        _output.Desktop.DxgiRing = null;
        return ring;
    }

    /// <summary>Releases the presenter at full output shutdown, after the live generation.</summary>
    private void DestroyDxgiPresenterForShutdown()
    {
        VulkanDxgiPresenter? presenter = _output.Desktop.DxgiPresenter;
        _output.Desktop.DxgiPresenter = null;
        presenter?.Dispose();
    }
}
